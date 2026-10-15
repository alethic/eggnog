namespace Eggnog.Tests.Predicates.Literal
{

    /// <summary>
    /// The predicate theory of <see cref="Scoped.PredicateTheory"/> with contexts as data arguments, in one flat graph
    /// that runs at its root. Every claim about a path takes a context (<see cref="Ctx"/>) as its first column:
    /// <c>(Schema)</c> holds what every document satisfies, <c>(Query)</c> adds one predicate's claims, and
    /// <c>(Assume c p v)</c> is the case of a split of <c>c</c> in which path <c>p</c> holds <c>v</c>. A child context
    /// inherits its parent's facts by <c>horn</c> rules over <c>parent</c>. Splits and lifts are rules of the
    /// <c>split</c> ruleset: a split opens a case per value of a present discriminator, a contradicted case removes its
    /// value from the domain, and a stored form, presence or domain that every remaining case reports is lifted to the
    /// split context. The theory has no integer domains and no upper numeric comparison.
    /// </summary>
    /// <remarks>
    /// The order in which tables are defined is part of the theory: extraction breaks ties in cost by function id,
    /// which is declaration order, so reordering the definitions can change which of two equal-cost predicates is
    /// extracted. A context's contradiction is <see cref="Contradicts"/>, named <c>contradicts</c>, because the
    /// graph's own nullary <see cref="EGraph.Contradiction"/> owns the name <c>contradiction</c>.
    /// </remarks>
    public sealed class ContextArgumentTheory
    {

        static readonly ValueSet<string> StringOrNull = ValueSet.Of("string", "null");

        /// <summary>
        /// Defines the theory in <paramref name="graph"/>.
        /// </summary>
        /// <param name="graph">The graph.</param>
        public ContextArgumentTheory(EGraph graph)
        {
            Graph = graph;
            graph.DefineSort<Ctx>("Ctx");
            Schema = graph.DefineConstructor<Ctx>("Schema");
            Query = graph.DefineConstructor<Ctx>("Query");
            Assume = graph.DefineConstructor<Ctx, string, string, Ctx>("Assume");
            Parent = graph.DefineRelation<Ctx, Ctx>("parent");

            Dom = graph.DefineFunction<Ctx, string, ValueSet<string>>("dom", ValueLattice.Intersect<string>());
            Types = graph.DefineFunction<Ctx, string, ValueSet<string>>("types", ValueLattice.Intersect<string>());
            Lo = graph.DefineFunction<Ctx, string, long>("lo", ValueLattice.Max<long>());
            Hi = graph.DefineFunction<Ctx, string, long>("hi", ValueLattice.Min<long>());
            Present = graph.DefineRelation<Ctx, string>("present");
            Form = graph.DefineRelation<Ctx, string, string>("form");
            Ne = graph.DefineRelation<Ctx, string, string>("ne");
            Contradicts = graph.DefineRelation<Ctx>("contradicts");
            Discriminator = graph.DefineRelation<string, string>("discriminator");

            FormCases = graph.DefineFunction<Ctx, string, string, string, ValueSet<string>>("form-cases", ValueLattice.Union<string>());
            PresentCases = graph.DefineFunction<Ctx, string, string, ValueSet<string>>("present-cases", ValueLattice.Union<string>());
            DomCases = graph.DefineFunction<Ctx, string, string, ValueSet<string>>("dom-cases", ValueLattice.Union<string>());
            DomReported = graph.DefineFunction<Ctx, string, string, ValueSet<string>>("dom-reported", ValueLattice.Union<string>());

            graph.DefineSort<Pred>("Pred");
            PTrue = graph.DefineConstructor<Pred>("PTrue", cost: 0);
            PFalse = graph.DefineConstructor<Pred>("PFalse", cost: 0);
            And = graph.DefineConstructor<Pred, Pred, Pred>("And");
            Or = graph.DefineConstructor<Pred, Pred, Pred>("Or");
            Not = graph.DefineConstructor<Pred, Pred>("Not");
            StrEq = graph.DefineConstructor<string, string, Pred>("StrEq");
            StrGe = graph.DefineConstructor<string, string, Pred>("StrGe");
            StrGt = graph.DefineConstructor<string, string, Pred>("StrGt");
            BoolTrue = graph.DefineConstructor<string, Pred>("BoolTrue");
            NumGt = graph.DefineConstructor<string, long, Pred>("NumGt");
            IsDefined = graph.DefineConstructor<string, Pred>("IsDefined");
            IsString = graph.DefineConstructor<string, Pred>("IsString");
            UuidEq = graph.DefineConstructor<string, string, Pred>("UuidEq", cost: 1000);
            InstantGt = graph.DefineConstructor<string, string, Pred>("InstantGt", cost: 1000);

            WhereClause = graph.DefineRelation<Pred>("where");
            Conjunct = graph.DefineRelation<Pred>("conjunct");
            InSet = graph.DefineFunction<Pred, string, ValueSet<string>>("inset", ValueLattice.Intersect<string>());
            Admits = graph.DefineFunction<Pred, string, ValueSet<string>>("admits", ValueLattice.Intersect<string>());

            Horn = graph.DefineRuleset("horn");
            Lattice = graph.DefineRuleset("lattice");
            Split = graph.DefineRuleset("split");
            Rewrite = graph.DefineRuleset("rewrite");
            DefineInheritance();
            DefineLattice();
            DefineSplit();
            DefinePredicates();
            DefineRewrite();
        }

        /// <summary>Gets the graph.</summary>
        public EGraph Graph { get; }

        /// <summary>Gets <c>(Schema)</c>: what every document satisfies.</summary>
        public Constructor<Ctx> Schema { get; }

        /// <summary>Gets <c>(Query)</c>: the schema plus the query predicate's conjuncts.</summary>
        public Constructor<Ctx> Query { get; }

        /// <summary>
        /// Gets <c>(Assume c p v)</c>: <c>c</c> plus "path p is present and holds v", one case of a split.
        /// </summary>
        public Constructor<Ctx, string, string, Ctx> Assume { get; }

        /// <summary>Gets <c>parent</c>: a child context inherits its parent's facts.</summary>
        public Relation<Ctx, Ctx> Parent { get; }

        /// <summary>Gets <c>dom</c>: the values a path can hold.</summary>
        public Function<Ctx, string, ValueSet<string>> Dom { get; }

        /// <summary>Gets <c>types</c>: the JSON types a path can hold.</summary>
        public Function<Ctx, string, ValueSet<string>> Types { get; }

        /// <summary>Gets <c>lo</c>: a lower bound on a numeric path.</summary>
        public Function<Ctx, string, long> Lo { get; }

        /// <summary>Gets <c>hi</c>: an upper bound on a numeric path.</summary>
        public Function<Ctx, string, long> Hi { get; }

        /// <summary>Gets <c>present</c>: the path is present.</summary>
        public Relation<Ctx, string> Present { get; }

        /// <summary>Gets <c>form</c>: the path's strings use this stored form.</summary>
        public Relation<Ctx, string, string> Form { get; }

        /// <summary>Gets <c>ne</c>: the path's value is not this.</summary>
        public Relation<Ctx, string, string> Ne { get; }

        /// <summary>Gets <c>contradicts</c>: no document is in this context.</summary>
        public Relation<Ctx> Contradicts { get; }

        /// <summary>Gets <c>discriminator</c>: a path a split may discriminate on, and one of its values.</summary>
        public Relation<string, string> Discriminator { get; }

        /// <summary>Gets <c>form-cases</c>: the values of p whose case proves a form of q.</summary>
        public Function<Ctx, string, string, string, ValueSet<string>> FormCases { get; }

        /// <summary>Gets <c>present-cases</c>: the values of p whose case proves q present.</summary>
        public Function<Ctx, string, string, ValueSet<string>> PresentCases { get; }

        /// <summary>Gets <c>dom-cases</c>: the union of the cases' domains of q.</summary>
        public Function<Ctx, string, string, ValueSet<string>> DomCases { get; }

        /// <summary>Gets <c>dom-reported</c>: the values of p whose case reported a domain of q.</summary>
        public Function<Ctx, string, string, ValueSet<string>> DomReported { get; }

        /// <summary>Gets the true predicate.</summary>
        public Constructor<Pred> PTrue { get; }

        /// <summary>Gets the false predicate.</summary>
        public Constructor<Pred> PFalse { get; }

        /// <summary>Gets conjunction.</summary>
        public Constructor<Pred, Pred, Pred> And { get; }

        /// <summary>Gets disjunction.</summary>
        public Constructor<Pred, Pred, Pred> Or { get; }

        /// <summary>Gets negation.</summary>
        public Constructor<Pred, Pred> Not { get; }

        /// <summary>Gets "path p holds the string v".</summary>
        public Constructor<string, string, Pred> StrEq { get; }

        /// <summary>Gets "path p holds a string ordered at or after v".</summary>
        public Constructor<string, string, Pred> StrGe { get; }

        /// <summary>Gets "path p holds a string ordered after v".</summary>
        public Constructor<string, string, Pred> StrGt { get; }

        /// <summary>Gets "path p holds true".</summary>
        public Constructor<string, Pred> BoolTrue { get; }

        /// <summary>Gets "path p holds a number greater than n".</summary>
        public Constructor<string, long, Pred> NumGt { get; }

        /// <summary>Gets "path p is present".</summary>
        public Constructor<string, Pred> IsDefined { get; }

        /// <summary>Gets "path p holds a string".</summary>
        public Constructor<string, Pred> IsString { get; }

        /// <summary>
        /// Gets "path p holds the UUID v" in any spelling: a costly comparison, which lowers to <see cref="StrEq"/>
        /// where the path's stored form is canonical lowercase (<c>uuid-lower</c>).
        /// </summary>
        public Constructor<string, string, Pred> UuidEq { get; }

        /// <summary>
        /// Gets "path p holds an instant after v": a costly comparison, which lowers to <see cref="StrGt"/> where the
        /// path's stored form is a fixed-width timestamp (<c>instant-ms</c>), whose string order is its time order.
        /// </summary>
        public Constructor<string, string, Pred> InstantGt { get; }

        /// <summary>Gets <c>where</c>: the query's predicate.</summary>
        public Relation<Pred> WhereClause { get; }

        /// <summary>Gets <c>conjunct</c>: the query predicate's top-level conjuncts.</summary>
        public Relation<Pred> Conjunct { get; }

        /// <summary>Gets <c>inset</c>: the predicate holds exactly when path p holds a value of S.</summary>
        public Function<Pred, string, ValueSet<string>> InSet { get; }

        /// <summary>Gets <c>admits</c>: the predicate implies that path p holds a value of S.</summary>
        public Function<Pred, string, ValueSet<string>> Admits { get; }

        /// <summary>Gets the horn ruleset.</summary>
        public Ruleset Horn { get; }

        /// <summary>Gets the lattice ruleset.</summary>
        public Ruleset Lattice { get; }

        /// <summary>Gets the split ruleset.</summary>
        public Ruleset Split { get; }

        /// <summary>Gets the rewrite ruleset.</summary>
        public Ruleset Rewrite { get; }

        /// <summary>
        /// Writes the theory's ground fact <c>(parent (Query) (Schema))</c> at the root: the query inherits the schema.
        /// </summary>
        public void Ground() => Graph.Root.Assume(Parent.Of(Query.Of(), Schema.Of()));

        /// <summary>
        /// Declares a guarded schema rule, in any context: where <paramref name="path"/> is present and holds only
        /// <paramref name="value"/>, <paramref name="formPath"/> has the stored form <paramref name="form"/>:
        /// <c>(rule ((present c p) (= (dom c p) (set-of v))) ((form c q f)) :ruleset horn)</c>.
        /// </summary>
        /// <param name="path">The guard's path.</param>
        /// <param name="value">The guard's value.</param>
        /// <param name="formPath">The path whose form the branch declares.</param>
        /// <param name="form">The stored form.</param>
        /// <returns>The rule.</returns>
        public Rule DeclareFormUnder(string path, string value, string formPath, string form) =>
            Horn.DefineRule($"{path} = {value} declares {formPath} {form}", r =>
            {
                var c = r.Var<Ctx>("c");
                r.When(Present.Of(c, path), Dom.Of(c, path).Is(ValueSet.Of(value)));
                r.Then(Form.Of(c, formPath, form));
            });

        void DefineInheritance()
        {
            Horn.DefineRule("inherit dom", r =>
            {
                var (k, c, p, d) = r.Vars<Ctx, Ctx, string, ValueSet<string>>("k", "c", "p", "d");
                r.When(Parent.Of(k, c), d.Is(Dom.Of(c, p)));
                r.Then(Dom.Of(k, p).Is(d));
            });
            Horn.DefineRule("inherit types", r =>
            {
                var (k, c, p, t) = r.Vars<Ctx, Ctx, string, ValueSet<string>>("k", "c", "p", "t");
                r.When(Parent.Of(k, c), t.Is(Types.Of(c, p)));
                r.Then(Types.Of(k, p).Is(t));
            });
            Horn.DefineRule("inherit lo", r =>
            {
                var (k, c, p, l) = r.Vars<Ctx, Ctx, string, long>("k", "c", "p", "l");
                r.When(Parent.Of(k, c), l.Is(Lo.Of(c, p)));
                r.Then(Lo.Of(k, p).Is(l));
            });
            Horn.DefineRule("inherit hi", r =>
            {
                var (k, c, p, h) = r.Vars<Ctx, Ctx, string, long>("k", "c", "p", "h");
                r.When(Parent.Of(k, c), h.Is(Hi.Of(c, p)));
                r.Then(Hi.Of(k, p).Is(h));
            });
            Horn.DefineRule("inherit present", r =>
            {
                var (k, c, p) = r.Vars<Ctx, Ctx, string>("k", "c", "p");
                r.When(Parent.Of(k, c), Present.Of(c, p));
                r.Then(Present.Of(k, p));
            });
            Horn.DefineRule("inherit form", r =>
            {
                var (k, c, p, f) = r.Vars<Ctx, Ctx, string, string>("k", "c", "p", "f");
                r.When(Parent.Of(k, c), Form.Of(c, p, f));
                r.Then(Form.Of(k, p, f));
            });
            Horn.DefineRule("inherit ne", r =>
            {
                var (k, c, p, v) = r.Vars<Ctx, Ctx, string, string>("k", "c", "p", "v");
                r.When(Parent.Of(k, c), Ne.Of(c, p, v));
                r.Then(Ne.Of(k, p, v));
            });
            Horn.DefineRule("inherit contradiction", r =>
            {
                var (k, c) = r.Vars<Ctx, Ctx>("k", "c");
                r.When(Parent.Of(k, c), Contradicts.Of(c));
                r.Then(Contradicts.Of(k));
            });
        }

        void DefineLattice()
        {
            Lattice.DefineRule("a domain settles the type", r =>
            {
                var (c, p, d) = r.Vars<Ctx, string, ValueSet<string>>("c", "p", "d");
                r.When(d.Is(Dom.Of(c, p)));
                r.Then(Types.Of(c, p).Is(ValueSet.Of("string")));
            });
            Lattice.DefineRule("a form describes strings", r =>
            {
                var (c, p, f) = r.Vars<Ctx, string, string>("c", "p", "f");
                r.When(Form.Of(c, p, f));
                r.Then(Types.Of(c, p).Is(StringOrNull));
            });
            Lattice.DefineRule("a disequality narrows a domain", r =>
            {
                var (c, p, v, d) = r.Vars<Ctx, string, string, ValueSet<string>>("c", "p", "v", "d");
                r.When(Ne.Of(c, p, v), d.Is(Dom.Of(c, p)));
                r.Where(Primitives.Contains<string>().Of(d, v));
                r.Then(Dom.Of(c, p).Is(Primitives.Remove<string>().Of(d, v)));
            });
            Lattice.DefineRule("an empty domain", r =>
            {
                var (c, p, d) = r.Vars<Ctx, string, ValueSet<string>>("c", "p", "d");
                r.When(Present.Of(c, p), d.Is(Dom.Of(c, p)), Primitives.Count<string>().Of(d).Is(0L));
                r.Then(Contradicts.Of(c));
            });
            Lattice.DefineRule("no possible type", r =>
            {
                var (c, p, t) = r.Vars<Ctx, string, ValueSet<string>>("c", "p", "t");
                r.When(Present.Of(c, p), t.Is(Types.Of(c, p)), Primitives.Count<string>().Of(t).Is(0L));
                r.Then(Contradicts.Of(c));
            });
            Lattice.DefineRule("crossed bounds", r =>
            {
                var (c, p) = r.Vars<Ctx, string>("c", "p");
                var (l, h) = r.Vars<long, long>("l", "h");
                r.When(Present.Of(c, p), l.Is(Lo.Of(c, p)), h.Is(Hi.Of(c, p)));
                r.Where(Primitives.Greater.Of(l, h));
                r.Then(Contradicts.Of(c));
            });
        }

        void DefineSplit()
        {
            Split.DefineRule("split on a discriminator", r =>
            {
                var (p, v, c, d) = r.Vars<string, string, Ctx, ValueSet<string>>("p", "v", "c", "d");
                r.When(Discriminator.Of(p, v), Present.Of(c, p), d.Is(Dom.Of(c, p)));
                r.Where(Primitives.Greater.Of(Primitives.Count<string>().Of(d), 1L));
                r.Where(Primitives.Contains<string>().Of(d, v));
                r.Then(
                    Parent.Of(Assume.Of(c, p, v), c),
                    Present.Of(Assume.Of(c, p, v), p),
                    Dom.Of(Assume.Of(c, p, v), p).Is(Primitives.Singleton<string>().Of(v)));
            });
            Split.DefineRule("a vacuous case removes its value", r =>
            {
                var (k, c, p, v) = r.Vars<Ctx, Ctx, string, string>("k", "c", "p", "v");
                var d = r.Var<ValueSet<string>>("d");
                r.When(k.Is(Assume.Of(c, p, v)), Contradicts.Of(k), d.Is(Dom.Of(c, p)));
                r.Where(Primitives.Contains<string>().Of(d, v));
                r.Then(Dom.Of(c, p).Is(Primitives.Remove<string>().Of(d, v)));
            });
            Split.DefineRule("report a stored form", r =>
            {
                var (k, c, p, v) = r.Vars<Ctx, Ctx, string, string>("k", "c", "p", "v");
                var (q, f) = r.Vars<string, string>("q", "f");
                r.When(k.Is(Assume.Of(c, p, v)), Form.Of(k, q, f));
                r.Then(FormCases.Of(c, p, q, f).Is(Primitives.Singleton<string>().Of(v)));
            });
            Split.DefineRule("lift a stored form", r =>
            {
                var (c, p, q, f) = r.Vars<Ctx, string, string, string>("c", "p", "q", "f");
                var (s, d) = r.Vars<ValueSet<string>, ValueSet<string>>("s", "d");
                r.When(s.Is(FormCases.Of(c, p, q, f)), Present.Of(c, p), d.Is(Dom.Of(c, p)));
                r.When(Primitives.Count<string>().Of(Primitives.Except<string>().Of(d, s)).Is(0L));
                r.Then(Form.Of(c, q, f));
            });
            Split.DefineRule("report presence", r =>
            {
                var (k, c, p, v) = r.Vars<Ctx, Ctx, string, string>("k", "c", "p", "v");
                var q = r.Var<string>("q");
                r.When(k.Is(Assume.Of(c, p, v)), Present.Of(k, q));
                r.Then(PresentCases.Of(c, p, q).Is(Primitives.Singleton<string>().Of(v)));
            });
            Split.DefineRule("lift presence", r =>
            {
                var (c, p, q) = r.Vars<Ctx, string, string>("c", "p", "q");
                var (s, d) = r.Vars<ValueSet<string>, ValueSet<string>>("s", "d");
                r.When(s.Is(PresentCases.Of(c, p, q)), Present.Of(c, p), d.Is(Dom.Of(c, p)));
                r.When(Primitives.Count<string>().Of(Primitives.Except<string>().Of(d, s)).Is(0L));
                r.Then(Present.Of(c, q));
            });
            Split.DefineRule("report a domain", r =>
            {
                var (k, c, p, v) = r.Vars<Ctx, Ctx, string, string>("k", "c", "p", "v");
                var (q, dq) = r.Vars<string, ValueSet<string>>("q", "dq");
                r.When(k.Is(Assume.Of(c, p, v)), dq.Is(Dom.Of(k, q)));
                r.Then(DomCases.Of(c, p, q).Is(dq), DomReported.Of(c, p, q).Is(Primitives.Singleton<string>().Of(v)));
            });
            Split.DefineRule("lift a domain", r =>
            {
                var (c, p, q) = r.Vars<Ctx, string, string>("c", "p", "q");
                var (reported, u, d) = r.Vars<ValueSet<string>, ValueSet<string>, ValueSet<string>>("r", "u", "d");
                r.When(reported.Is(DomReported.Of(c, p, q)), u.Is(DomCases.Of(c, p, q)), Present.Of(c, p), d.Is(Dom.Of(c, p)));
                r.When(Primitives.Count<string>().Of(Primitives.Except<string>().Of(d, reported)).Is(0L));
                r.Then(Dom.Of(c, q).Is(u));
            });
        }

        void DefinePredicates()
        {
            Horn.DefineRule("a where clause is a conjunct", r =>
            {
                var e = r.Var<Pred>("e");
                r.When(WhereClause.Of(e));
                r.Then(Conjunct.Of(e));
            });
            Horn.DefineRule("conjuncts of and", r =>
            {
                var (a, b) = r.Vars<Pred, Pred>("a", "b");
                r.When(Conjunct.Of(And.Of(a, b)));
                r.Then(Conjunct.Of(a), Conjunct.Of(b));
            });
            Horn.DefineRule("inset of equality", r =>
            {
                var (e, p, v) = r.Vars<Pred, string, string>("e", "p", "v");
                r.When(e.Is(StrEq.Of(p, v)));
                r.Then(InSet.Of(e, p).Is(Primitives.Singleton<string>().Of(v)));
            });
            Horn.DefineRule("inset of or", r =>
            {
                var (e, a, b, p) = r.Vars<Pred, Pred, Pred, string>("e", "a", "b", "p");
                var (sa, sb) = r.Vars<ValueSet<string>, ValueSet<string>>("sa", "sb");
                r.When(e.Is(Or.Of(a, b)), sa.Is(InSet.Of(a, p)), sb.Is(InSet.Of(b, p)));
                r.Then(InSet.Of(e, p).Is(Primitives.Union<string>().Of(sa, sb)));
            });
            Horn.DefineRule("inset admits", r =>
            {
                var (e, p, s) = r.Vars<Pred, string, ValueSet<string>>("e", "p", "s");
                r.When(s.Is(InSet.Of(e, p)));
                r.Then(Admits.Of(e, p).Is(s));
            });
            Horn.DefineRule("and admits left", r =>
            {
                var (e, a, b, p) = r.Vars<Pred, Pred, Pred, string>("e", "a", "b", "p");
                var s = r.Var<ValueSet<string>>("s");
                r.When(e.Is(And.Of(a, b)), s.Is(Admits.Of(a, p)));
                r.Then(Admits.Of(e, p).Is(s));
            });
            Horn.DefineRule("and admits right", r =>
            {
                var (e, a, b, p) = r.Vars<Pred, Pred, Pred, string>("e", "a", "b", "p");
                var s = r.Var<ValueSet<string>>("s");
                r.When(e.Is(And.Of(a, b)), s.Is(Admits.Of(b, p)));
                r.Then(Admits.Of(e, p).Is(s));
            });
            Horn.DefineRule("admits of or", r =>
            {
                var (e, a, b, p) = r.Vars<Pred, Pred, Pred, string>("e", "a", "b", "p");
                var (sa, sb) = r.Vars<ValueSet<string>, ValueSet<string>>("sa", "sb");
                r.When(e.Is(Or.Of(a, b)), sa.Is(Admits.Of(a, p)), sb.Is(Admits.Of(b, p)));
                r.Then(Admits.Of(e, p).Is(Primitives.Union<string>().Of(sa, sb)));
            });
            Horn.DefineRule("a conjunct admits", r =>
            {
                var (e, p, s) = r.Vars<Pred, string, ValueSet<string>>("e", "p", "s");
                r.When(Conjunct.Of(e), s.Is(Admits.Of(e, p)));
                r.Then(Present.Of(Query.Of(), p), Dom.Of(Query.Of(), p).Is(s));
            });
            Horn.DefineRule("a negated equality", r =>
            {
                var (p, v) = r.Vars<string, string>("p", "v");
                r.When(Conjunct.Of(Not.Of(StrEq.Of(p, v))));
                r.Then(Present.Of(Query.Of(), p), Ne.Of(Query.Of(), p, v));
            });
            foreach (var comparison in new[] { UuidEq, StrGe, InstantGt })
            {
                Horn.DefineRule($"{comparison.Name} proves presence", r =>
                {
                    var (p, v) = r.Vars<string, string>("p", "v");
                    r.When(Conjunct.Of(comparison.Of(p, v)));
                    r.Then(Present.Of(Query.Of(), p));
                });
            }

            Horn.DefineRule("a numeric lower bound", r =>
            {
                var (p, n) = r.Vars<string, long>("p", "n");
                r.When(Conjunct.Of(NumGt.Of(p, n)));
                r.Then(
                    Present.Of(Query.Of(), p),
                    Lo.Of(Query.Of(), p).Is(Primitives.Add.Of(n, 1L)),
                    Types.Of(Query.Of(), p).Is(ValueSet.Of("number")));
            });
        }

        void DefineRewrite()
        {
            Rewrite.DefineRule("lower uuid", r =>
            {
                var (e, p, v) = r.Vars<Pred, string, string>("e", "p", "v");
                r.When(e.Is(UuidEq.Of(p, v)), Form.Of(Query.Of(), p, "uuid-lower"));
                r.Then(e.Is(StrEq.Of(p, v)));
            });
            Rewrite.DefineRule("lower instant", r =>
            {
                var (e, p, v) = r.Vars<Pred, string, string>("e", "p", "v");
                r.When(e.Is(InstantGt.Of(p, v)), Form.Of(Query.Of(), p, "instant-ms"));
                r.Then(e.Is(StrGt.Of(p, v)));
            });
            Rewrite.DefineRule("contradicted predicate", r =>
            {
                var e = r.Var<Pred>("e");
                r.When(WhereClause.Of(e), Contradicts.Of(Query.Of()));
                r.Then(e.Is(PFalse.Of()));
            });
            Rewrite.DefineRule("declared tautology", r =>
            {
                var (e, p) = r.Vars<Pred, string>("e", "p");
                var (s, d) = r.Vars<ValueSet<string>, ValueSet<string>>("s", "d");
                r.When(s.Is(InSet.Of(e, p)), Present.Of(Schema.Of(), p), d.Is(Dom.Of(Schema.Of(), p)));
                r.When(Primitives.Count<string>().Of(Primitives.Except<string>().Of(d, s)).Is(0L));
                r.Then(e.Is(PTrue.Of()));
            });
            Rewrite.DefineRule("a string path's negated string test", r =>
            {
                var (e, p, t) = r.Vars<Pred, string, ValueSet<string>>("e", "p", "t");
                r.When(e.Is(Not.Of(IsString.Of(p))), t.Is(Types.Of(Query.Of(), p)));
                r.When(Primitives.Count<string>().Of(Primitives.Except<string>().Of(t, StringOrNull)).Is(0L));
                r.Then(e.Is(Not.Of(IsDefined.Of(p))));
            });
            Rewrite.DefineRule("a definedness test absorbs its negation", r =>
            {
                var (p, x) = r.Vars<string, Pred>("p", "x");
                r.Rewrite(And.Of(IsDefined.Of(p), Or.Of(Not.Of(IsDefined.Of(p)), x)), And.Of(IsDefined.Of(p), x));
            });
            Rewrite.DefineRule("StrGe implies defined", r =>
            {
                var (p, v) = r.Vars<string, string>("p", "v");
                r.Rewrite(And.Of(IsDefined.Of(p), StrGe.Of(p, v)), StrGe.Of(p, v));
            });
            Rewrite.DefineRule("StrEq implies defined", r =>
            {
                var (p, v) = r.Vars<string, string>("p", "v");
                r.Rewrite(And.Of(IsDefined.Of(p), StrEq.Of(p, v)), StrEq.Of(p, v));
            });
            Rewrite.DefineRule("declared presence", r =>
            {
                var (e, p) = r.Vars<Pred, string>("e", "p");
                r.When(e.Is(IsDefined.Of(p)), Present.Of(Schema.Of(), p));
                r.Then(e.Is(PTrue.Of()));
            });
            Rewrite.DefineRule("and commutes", r =>
            {
                var (a, b) = r.Vars<Pred, Pred>("a", "b");
                r.Rewrite(And.Of(a, b), And.Of(b, a));
            });
            Rewrite.DefineRule("or commutes", r =>
            {
                var (a, b) = r.Vars<Pred, Pred>("a", "b");
                r.Rewrite(Or.Of(a, b), Or.Of(b, a));
            });
            Rewrite.DefineRule("true and", r =>
            {
                var a = r.Var<Pred>("a");
                r.Rewrite(And.Of(PTrue.Of(), a), a);
            });
            Rewrite.DefineRule("false and", r =>
            {
                var a = r.Var<Pred>("a");
                r.Rewrite(And.Of(PFalse.Of(), a), PFalse.Of());
            });
            Rewrite.DefineRule("false or", r =>
            {
                var a = r.Var<Pred>("a");
                r.Rewrite(Or.Of(PFalse.Of(), a), a);
            });
            Rewrite.DefineRule("true or", r =>
            {
                var a = r.Var<Pred>("a");
                r.Rewrite(Or.Of(PTrue.Of(), a), PTrue.Of());
            });
        }

    }

}

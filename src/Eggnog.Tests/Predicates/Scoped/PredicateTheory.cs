namespace Eggnog.Tests.Predicates.Scoped
{

    /// <summary>
    /// A theory of predicates over document paths, in which scopes are the contexts. Its facts are claims about a
    /// path: presence, stored forms, refused values, string domains, JSON types, numeric bounds and integer domains.
    /// Its terms are predicates (<see cref="Pred"/>) over paths. Its rules derive what a predicate's conjuncts claim,
    /// settle the lattices, report a contradiction when no document can satisfy the claims, lower costly comparisons
    /// and simplify the predicate. A schema scope (<see cref="Schema"/>) holds what every document satisfies; a query
    /// scope (<see cref="Query"/>) under it adds one predicate; the cases of a discriminator split
    /// (<see cref="Case"/>) assume one value each. Contexts carry no data argument and facts flow to a child by scope
    /// inheritance (<see cref="Literal.ContextArgumentTheory"/> is the same theory with both written out). The theory
    /// is defined once per graph and shared by every schema scope in it; there is one per graph, because it defines the
    /// graph's scope tags.
    /// </summary>
    public sealed partial class PredicateTheory
    {

        static readonly Set<string> StringOrNull = Set.Of("string", "null");

        /// <summary>
        /// Defines the theory in a graph. Every rule that reads a scope is defined here, before any schema is frozen:
        /// the first read of a table in a scope creates its mirror, which is refused once a scope of that tag is
        /// frozen.
        /// </summary>
        /// <param name="graph">The graph.</param>
        public PredicateTheory(EGraph graph)
        {
            Graph = graph;
            graph.DefineSort<Pred>("Pred");

            // The graph's own tags (a tag belongs to the graph that defines it): a schema scope, and a query scope.
            Schema = graph.DefineScopeTag("schema");
            Query = graph.DefineScopeTag("query");

            // Claims about a path. Value-only, so a query can read the schema's view of each: a scoped read may name
            // only a value-only table, which it reads through a mirror.
            Present = graph.DefineRelation<string>("present");
            Form = graph.DefineRelation<string, string>("form");
            Ne = graph.DefineRelation<string, string>("ne");
            Discriminator = graph.DefineRelation<string>("discriminator");
            Dom = graph.DefineFunction<string, Set<string>>("dom", ValueLattice.Intersect<string>());
            Types = graph.DefineFunction<string, Set<string>>("types", ValueLattice.Intersect<string>());
            Lo = graph.DefineFunction<string, long>("lo", ValueLattice.Max<long>());
            Hi = graph.DefineFunction<string, long>("hi", ValueLattice.Min<long>());
            IDom = graph.DefineFunction<string, Set<long>>("idom", ValueLattice.Intersect<long>());

            // Predicates. Truth and falsehood cost nothing, and the UUID and instant comparisons cost 1000, so that
            // extraction prefers the string comparisons they lower to.
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
            NumLt = graph.DefineConstructor<string, long, Pred>("NumLt");
            IsDefined = graph.DefineConstructor<string, Pred>("IsDefined");
            IsString = graph.DefineConstructor<string, Pred>("IsString");
            UuidEq = graph.DefineConstructor<string, string, Pred>("UuidEq", cost: 1000);
            InstantGt = graph.DefineConstructor<string, string, Pred>("InstantGt", cost: 1000);

            // Query bookkeeping is never lifted; the analyses are derived from terms alone.
            var queryOnly = new TableOptions { Lift = false };
            var analysis = new TableOptions { Role = TableRole.Analysis };
            WhereClause = graph.DefineRelation<Pred>("where", queryOnly);
            Conjunct = graph.DefineRelation<Pred>("conjunct", queryOnly);
            InSet = graph.DefineFunction<Pred, string, Set<string>>("inset", ValueLattice.Intersect<string>(), analysis);
            Admits = graph.DefineFunction<Pred, string, Set<string>>("admits", ValueLattice.Intersect<string>(), analysis);

            Horn = graph.DefineRuleset("horn");
            Lattice = graph.DefineRuleset("lattice");
            // Every rewrite reads facts only from the schema, so that a conjunct never proves itself redundant, unless
            // the rule says why it may read the query's.
            Rewrite = graph.DefineRuleset("rewrite", new RulesetOptions { Licence = Schema });
            DefineHorn();
            DefineLattice();
            DefineRewrite();
            DefineCases();
        }

        /// <summary>Defines the split members (PredicateTheory.Cases.cs).</summary>
        partial void DefineCases();

        /// <summary>Gets the graph.</summary>
        public EGraph Graph { get; }

        /// <summary>Gets the tag of a schema scope: what every document satisfies.</summary>
        public ScopeTag Schema { get; }

        /// <summary>Gets the tag of a query scope: its schema plus one predicate's claims.</summary>
        public ScopeTag Query { get; }

        /// <summary>Gets the relation "the path is present".</summary>
        public Relation<string> Present { get; }

        /// <summary>Gets the relation "the path's strings use this stored form".</summary>
        public Relation<string, string> Form { get; }

        /// <summary>Gets the relation "the path's value is not this".</summary>
        public Relation<string, string> Ne { get; }

        /// <summary>Gets the paths a split may discriminate on.</summary>
        public Relation<string> Discriminator { get; }

        /// <summary>Gets the values a path can hold, if present.</summary>
        public Function<string, Set<string>> Dom { get; }

        /// <summary>Gets the JSON types a path can hold, if present.</summary>
        public Function<string, Set<string>> Types { get; }

        /// <summary>Gets a lower bound on a numeric path.</summary>
        public Function<string, long> Lo { get; }

        /// <summary>Gets an upper bound on a numeric path.</summary>
        public Function<string, long> Hi { get; }

        /// <summary>Gets the integers a numeric path can hold, if present.</summary>
        public Function<string, Set<long>> IDom { get; }

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

        /// <summary>Gets "path p holds a number less than n".</summary>
        public Constructor<string, long, Pred> NumLt { get; }

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

        /// <summary>Gets the query's predicate.</summary>
        public Relation<Pred> WhereClause { get; }

        /// <summary>Gets the query predicate's top-level conjuncts.</summary>
        public Relation<Pred> Conjunct { get; }

        /// <summary>Gets "the predicate holds exactly when path p holds a value of S".</summary>
        public Function<Pred, string, Set<string>> InSet { get; }

        /// <summary>Gets "the predicate implies that path p holds a value of S".</summary>
        public Function<Pred, string, Set<string>> Admits { get; }

        /// <summary>Gets the Horn ruleset.</summary>
        public Ruleset Horn { get; }

        /// <summary>Gets the lattice ruleset.</summary>
        public Ruleset Lattice { get; }

        /// <summary>
        /// Gets the rewrite ruleset, licensed by <see cref="Schema"/>: its rules read facts from the schema, unless a
        /// rule says why it may read the query's own.
        /// </summary>
        public Ruleset Rewrite { get; }

        /// <summary>
        /// Declares one branch of a discriminated union: where the path holds the value, the consequences hold. The
        /// rule is local to the schema scope, so schema scopes sharing this theory never see each other's branches.
        /// </summary>
        /// <param name="schema">The schema scope, not yet frozen.</param>
        /// <param name="path">The discriminator path.</param>
        /// <param name="value">The branch's value.</param>
        /// <param name="consequences">What the branch declares.</param>
        /// <returns>The rule.</returns>
        public Rule DeclareBranch(Scope schema, string path, string value, params Fact[] consequences) =>
            schema.DefineRule(Horn, $"{path} = {value}", r =>
            {
                r.When(Present.Of(path), Dom.Of(path).Is(Set.Of(value)));
                r.Then(consequences);
            });

        void DefineHorn()
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
                var (sa, sb) = r.Vars<Set<string>, Set<string>>("sa", "sb");
                r.When(e.Is(Or.Of(a, b)), sa.Is(InSet.Of(a, p)), sb.Is(InSet.Of(b, p)));
                r.Then(InSet.Of(e, p).Is(Primitives.Union<string>().Of(sa, sb)));
            });
            Horn.DefineRule("inset admits", r =>
            {
                var (e, p, s) = r.Vars<Pred, string, Set<string>>("e", "p", "s");
                r.When(s.Is(InSet.Of(e, p)));
                r.Then(Admits.Of(e, p).Is(s));
            });
            foreach (var left in new[] { true, false })
            {
                Horn.DefineRule(left ? "and admits left" : "and admits right", r =>
                {
                    var (e, a, b, p) = r.Vars<Pred, Pred, Pred, string>("e", "a", "b", "p");
                    var s = r.Var<Set<string>>("s");
                    r.When(e.Is(And.Of(a, b)), s.Is(Admits.Of(left ? a : b, p)));
                    r.Then(Admits.Of(e, p).Is(s));
                });
            }
            Horn.DefineRule("admits of or", r =>
            {
                var (e, a, b, p) = r.Vars<Pred, Pred, Pred, string>("e", "a", "b", "p");
                var (sa, sb) = r.Vars<Set<string>, Set<string>>("sa", "sb");
                r.When(e.Is(Or.Of(a, b)), sa.Is(Admits.Of(a, p)), sb.Is(Admits.Of(b, p)));
                r.Then(Admits.Of(e, p).Is(Primitives.Union<string>().Of(sa, sb)));
            });

            // What a conjunct establishes, written to the running scope: the query.
            Horn.DefineRule("a conjunct admits", r =>
            {
                var (e, p, s) = r.Vars<Pred, string, Set<string>>("e", "p", "s");
                r.When(Conjunct.Of(e), s.Is(Admits.Of(e, p)));
                r.Then(Present.Of(p), Dom.Of(p).Is(s));
            });
            Horn.DefineRule("a negated equality", r =>
            {
                var (p, v) = r.Vars<string, string>("p", "v");
                r.When(Conjunct.Of(Not.Of(StrEq.Of(p, v))));
                r.Then(Present.Of(p), Ne.Of(p, v));
            });
            foreach (var comparison in new[] { UuidEq, StrGe, InstantGt })
            {
                Horn.DefineRule($"{comparison.Name} proves presence", r =>
                {
                    var (p, v) = r.Vars<string, string>("p", "v");
                    r.When(Conjunct.Of(comparison.Of(p, v)));
                    r.Then(Present.Of(p));
                });
            }
            Horn.DefineRule("a numeric lower bound", r =>
            {
                var (p, n) = r.Vars<string, long>("p", "n");
                r.When(Conjunct.Of(NumGt.Of(p, n)));
                r.Then(Present.Of(p), Lo.Of(p).Is(Primitives.Add.Of(n, 1L)), Types.Of(p).Is(Set.Of("number")));
            });
            Horn.DefineRule("a numeric upper bound", r =>
            {
                var (p, n) = r.Vars<string, long>("p", "n");
                r.When(Conjunct.Of(NumLt.Of(p, n)));
                r.Then(Present.Of(p), Hi.Of(p).Is(Primitives.Subtract.Of(n, 1L)), Types.Of(p).Is(Set.Of("number")));
            });
        }

        void DefineLattice()
        {
            Lattice.DefineRule("a domain settles the type", r =>
            {
                var (p, d) = r.Vars<string, Set<string>>("p", "d");
                r.When(d.Is(Dom.Of(p)));
                r.Then(Types.Of(p).Is(Set.Of("string")));
            });
            Lattice.DefineRule("a form describes strings", r =>
            {
                var (p, f) = r.Vars<string, string>("p", "f");
                r.When(Form.Of(p, f));
                r.Then(Types.Of(p).Is(StringOrNull));
            });
            Lattice.DefineRule("a disequality narrows a domain", r =>
            {
                var (p, v, d) = r.Vars<string, string, Set<string>>("p", "v", "d");
                r.When(Ne.Of(p, v), d.Is(Dom.Of(p)));
                r.Where(Primitives.Contains<string>().Of(d, v));
                r.Then(Dom.Of(p).Is(Primitives.Remove<string>().Of(d, v)));
            });
            Lattice.DefineRule("a lower bound narrows an integer domain", r =>
            {
                var (p, d, l) = r.Vars<string, Set<long>, long>("p", "d", "l");
                r.When(d.Is(IDom.Of(p)), l.Is(Lo.Of(p)));
                r.Then(IDom.Of(p).Is(Compute.Of(d, l, (d, l) => d.Where(x => x >= l))));
            });
            Lattice.DefineRule("an upper bound narrows an integer domain", r =>
            {
                var (p, d, h) = r.Vars<string, Set<long>, long>("p", "d", "h");
                r.When(d.Is(IDom.Of(p)), h.Is(Hi.Of(p)));
                r.Then(IDom.Of(p).Is(Compute.Of(d, h, (d, h) => d.Where(x => x <= h))));
            });
            Lattice.DefineRule("an empty domain", r =>
            {
                var (p, d) = r.Vars<string, Set<string>>("p", "d");
                r.When(Present.Of(p), d.Is(Dom.Of(p)));
                r.Where(Primitives.IsEmpty<string>().Of(d));
                r.Then(Graph.Contradiction.Of());
            });
            Lattice.DefineRule("an empty integer domain", r =>
            {
                var (p, d) = r.Vars<string, Set<long>>("p", "d");
                r.When(Present.Of(p), d.Is(IDom.Of(p)));
                r.Where(Primitives.IsEmpty<long>().Of(d));
                r.Then(Graph.Contradiction.Of());
            });
            Lattice.DefineRule("no possible type", r =>
            {
                var (p, t) = r.Vars<string, Set<string>>("p", "t");
                r.When(Present.Of(p), t.Is(Types.Of(p)));
                r.Where(Primitives.IsEmpty<string>().Of(t));
                r.Then(Graph.Contradiction.Of());
            });
            Lattice.DefineRule("crossed bounds", r =>
            {
                var (p, l, h) = r.Vars<string, long, long>("p", "l", "h");
                r.When(Present.Of(p), l.Is(Lo.Of(p)), h.Is(Hi.Of(p)));
                r.Where(Primitives.Greater.Of(l, h));
                r.Then(Graph.Contradiction.Of());
            });
        }

        void DefineRewrite()
        {
            // These four judge on the query's own facts, and each says why that is sound.
            Rewrite.DefineRule("lower uuid", r =>
            {
                var (e, p, v) = r.Vars<Pred, string, string>("e", "p", "v");
                r.ReadsOwnFacts("the guard that proved the form is a conjunct that stays in the predicate");
                r.When(e.Is(UuidEq.Of(p, v)), Form.Of(p, "uuid-lower"));
                r.Then(e.Is(StrEq.Of(p, v)));
            });
            Rewrite.DefineRule("lower instant", r =>
            {
                var (e, p, v) = r.Vars<Pred, string, string>("e", "p", "v");
                r.ReadsOwnFacts("the guard that proved the form is a conjunct that stays in the predicate");
                r.When(e.Is(InstantGt.Of(p, v)), Form.Of(p, "instant-ms"));
                r.Then(e.Is(StrGt.Of(p, v)));
            });
            Rewrite.DefineRule("contradicted predicate", r =>
            {
                var e = r.Var<Pred>("e");
                r.ReadsOwnFacts("a query with no documents makes every predicate equivalent to false");
                r.When(WhereClause.Of(e), Graph.Contradiction.Of());
                r.Then(e.Is(PFalse.Of()));
            });
            // Not(IsString p) admits nothing a present string-or-null path holds: it is Not(IsDefined p).
            Rewrite.DefineRule("a string path's negated string test", r =>
            {
                var (e, p, t) = r.Vars<Pred, string, Set<string>>("e", "p", "t");
                r.ReadsOwnFacts("an equivalence under type facts whose conjuncts stay in the predicate");
                r.When(e.Is(Not.Of(IsString.Of(p))), t.Is(Types.Of(p)));
                r.Where(t, t => t.IsSubsetOf(StringOrNull));
                r.Then(e.Is(Not.Of(IsDefined.Of(p))));
            });

            // Deletions read facts only from the schema, and the ruleset's licence checks it: a conjunct's own
            // consequences in the query (present(kind) from StrEq(kind, A)) can never delete it. present@schema and
            // dom@schema are mirrors.
            Rewrite.DefineRule("declared tautology", r =>
            {
                var (e, p) = r.Vars<Pred, string>("e", "p");
                var (s, d) = r.Vars<Set<string>, Set<string>>("s", "d");
                r.When(s.Is(InSet.Of(e, p)), Present.In(Schema).Of(p), d.Is(Dom.In(Schema).Of(p)));
                r.Where(Primitives.IsSubsetOf<string>().Of(d, s));
                r.Then(e.Is(PTrue.Of()));
            });
            Rewrite.DefineRule("declared presence", r =>
            {
                var (e, p) = r.Vars<Pred, string>("e", "p");
                r.When(e.Is(IsDefined.Of(p)), Present.In(Schema).Of(p));
                r.Then(e.Is(PTrue.Of()));
            });

            // These read terms only, so the licence does not constrain them.
            Rewrite.DefineRule("a definedness test absorbs its negation", r =>
            {
                var (p, x) = r.Vars<string, Pred>("p", "x");
                r.Rewrite(And.Of(IsDefined.Of(p), Or.Of(Not.Of(IsDefined.Of(p)), x)), And.Of(IsDefined.Of(p), x));
            });
            foreach (var comparison in new[] { StrGe, StrEq })
            {
                Rewrite.DefineRule($"{comparison.Name} implies defined", r =>
                {
                    var (p, v) = r.Vars<string, string>("p", "v");
                    r.Rewrite(And.Of(IsDefined.Of(p), comparison.Of(p, v)), comparison.Of(p, v));
                });
            }

            // Boolean simplification.
            Rewrite.DefineRule("and commutes", r => { var (a, b) = r.Vars<Pred, Pred>("a", "b"); r.Rewrite(And.Of(a, b), And.Of(b, a)); });
            Rewrite.DefineRule("or commutes", r => { var (a, b) = r.Vars<Pred, Pred>("a", "b"); r.Rewrite(Or.Of(a, b), Or.Of(b, a)); });
            Rewrite.DefineRule("true and", r => { var a = r.Var<Pred>("a"); r.Rewrite(And.Of(PTrue.Of(), a), a); });
            Rewrite.DefineRule("false and", r => { var a = r.Var<Pred>("a"); r.Rewrite(And.Of(PFalse.Of(), a), PFalse.Of()); });
            Rewrite.DefineRule("false or", r => { var a = r.Var<Pred>("a"); r.Rewrite(Or.Of(PFalse.Of(), a), a); });
            Rewrite.DefineRule("true or", r => { var a = r.Var<Pred>("a"); r.Rewrite(Or.Of(PTrue.Of(), a), PTrue.Of()); });
        }

    }

}

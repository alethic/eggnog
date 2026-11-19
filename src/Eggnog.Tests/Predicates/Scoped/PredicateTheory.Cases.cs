using System;

namespace Eggnog.Tests.Predicates.Scoped
{

    public sealed partial class PredicateTheory
    {

        /// <summary>
        /// Gets the paths the running scope's predicate terms mention, derived from the terms alone.
        /// </summary>
        public Relation<string> Mentioned { get; private set; } = null!;

        /// <summary>
        /// Gets <c>form-cases</c>: the values of discriminator p whose branch declares that path q has stored form f.
        /// </summary>
        public Function<string, string, string, Set<string>> FormCases { get; private set; } = null!;

        /// <summary>Gets <c>present-cases</c>: the values of discriminator p whose branch declares q present.</summary>
        public Function<string, string, Set<string>> PresentCases { get; private set; } = null!;

        /// <summary>
        /// Gets <c>string-cases</c>: the values of discriminator p whose branch declares a stored form or a domain of q,
        /// by which q holds a string or null.
        /// </summary>
        public Function<string, string, Set<string>> StringCases { get; private set; } = null!;

        /// <summary>
        /// Gets <c>case-dom</c>: the values path q can hold, if present, where discriminator p holds v: the domain that
        /// v's branch declares, narrowed by what the running scope holds of q.
        /// </summary>
        public Function<string, string, string, Set<string>> CaseDom { get; private set; } = null!;

        /// <summary>
        /// Gets <c>dom-cases</c>: for discriminator p and its domain d, the union of the case domains of q
        /// (<see cref="CaseDom"/>) of the values of d that have one.
        /// </summary>
        /// <remarks>
        /// A row is keyed by the domain p holds when the row is written, so each domain of several values that p holds in
        /// turn adds a row for each path q a value of it declares a domain of, and the rows of the domains before it stay
        /// in the scope, never read again, until the scope is disposed: a query that narrows p in many steps keeps a row
        /// per step and path.
        /// </remarks>
        public Function<string, Set<string>, string, Set<string>> DomCases { get; private set; } = null!;

        /// <summary>
        /// Gets <c>dom-reported</c>: for discriminator p and its domain d, the values of d that have a case domain of q,
        /// which <see cref="DomCases"/> unions.
        /// </summary>
        /// <remarks>
        /// Keyed as <see cref="DomCases"/> is: the rows of the domains p held before stay in the scope as long.
        /// </remarks>
        public Function<string, Set<string>, string, Set<string>> DomReported { get; private set; } = null!;

        /// <summary>
        /// Gets the case rules a schema is compiled with: wherever a present discriminator's domain still holds more
        /// than one value, they reason by its values (<see cref="DefineCaseRules"/>).
        /// </summary>
        public Ruleset SchemaCases { get; private set; } = null!;

        /// <summary>
        /// Gets the case rules a query runs: those of <see cref="SchemaCases"/>, over the present discriminators its
        /// predicate mentions only, so that a query does not reason again by the cases of every discriminator the
        /// frozen schema already reasoned by.
        /// </summary>
        public Ruleset QueryCases { get; private set; } = null!;

        /// <summary>
        /// Gets the schedule a schema is compiled with: horn, lattice and the schema's case rules, then the copies of its
        /// facts (<see cref="Copies"/>).
        /// </summary>
        public Schedule Facts { get; private set; } = null!;

        /// <summary>
        /// Gets the schedule a query runs: horn, lattice, the query's case rules and rewrites, saturated together.
        /// </summary>
        public Schedule Saturation { get; private set; } = null!;

        /// <summary>
        /// Defines the case members: <see cref="Mentioned"/> and the rules that derive it, the tables a branch declares
        /// its claims in and the case rules derive from them, the schema's and the query's case rules, and the schedules
        /// that take them.
        /// </summary>
        partial void DefineCases()
        {
            // A path is mentioned when a comparison or test over it is a term of the running scope.
            Mentioned = Graph.DefineRelation<string>("mentioned");
            foreach (var comparison in new[] { StrEq, StrGe, StrGt, UuidEq, InstantGt })
            {
                Horn.DefineRule($"{comparison.Name} mentions its path", r =>
                {
                    var (p, v) = r.Vars<string, string>("p", "v");
                    r.When(Fact.Exists(comparison.Of(p, v)));
                    r.Then(Mentioned.Of(p));
                });
            }

            foreach (var test in new[] { BoolTrue, IsDefined, IsString })
            {
                Horn.DefineRule($"{test.Name} mentions its path", r =>
                {
                    var p = r.Var<string>("p");
                    r.When(Fact.Exists(test.Of(p)));
                    r.Then(Mentioned.Of(p));
                });
            }

            foreach (var bound in new[] { NumGt, NumLt })
            {
                Horn.DefineRule($"{bound.Name} mentions its path", r =>
                {
                    var (p, n) = r.Vars<string, long>("p", "n");
                    r.When(Fact.Exists(bound.Of(p, n)));
                    r.Then(Mentioned.Of(p));
                });
            }

            // What the branches declare, by the values whose branch declares it: sets that only grow. A domain is kept
            // per value, and narrows as a domain does; the union of a discriminator's cases is keyed by its domain, so
            // that a narrower domain unions only its own values.
            FormCases = Graph.DefineFunction<string, string, string, Set<string>>("form-cases", ValueLattice.Union<string>());
            PresentCases = Graph.DefineFunction<string, string, Set<string>>("present-cases", ValueLattice.Union<string>());
            StringCases = Graph.DefineFunction<string, string, Set<string>>("string-cases", ValueLattice.Union<string>());
            CaseDom = Graph.DefineFunction<string, string, string, Set<string>>("case-dom", ValueLattice.Intersect<string>());
            DomCases = Graph.DefineFunction<string, Set<string>, string, Set<string>>("dom-cases", ValueLattice.Union<string>());
            DomReported = Graph.DefineFunction<string, Set<string>, string, Set<string>>("dom-reported", ValueLattice.Union<string>());

            // A schema reasons by the cases of every present discriminator; a query only by those it mentions, since
            // the schema has derived already what the schema alone implies.
            SchemaCases = Graph.DefineRuleset("schema cases");
            QueryCases = Graph.DefineRuleset("query cases");
            DefineCaseRules(SchemaCases, mentioned: false);
            DefineCaseRules(QueryCases, mentioned: true);

            var settle = Schedule.Saturate(Horn, Lattice);
            Facts = Schedule.Sequence(Schedule.Saturate(settle, SchemaCases), Schedule.Saturate(Copies));
            Saturation = Schedule.Saturate(settle, QueryCases, Rewrite);
        }

        /// <summary>
        /// Defines the case rules of one ruleset. Where discriminator p is present and its domain d holds more than one
        /// value, p holds one value of d, and where it holds v the claims of v's branch hold
        /// (<see cref="DeclareBranch"/>). So a value is dropped from d when the scope rules out its branch's claims, by
        /// what the lattice rules refute of a present path: a path the branch declares present that the scope leaves no
        /// possible type, an empty domain or an empty integer domain, or whose bounds the scope crosses; and, where the
        /// path is present in the scope or declared present by the branch, a stored form of it whose types in the scope
        /// hold neither a string nor null, or a domain of it whose types hold no string, or that the scope's domain of the
        /// path and its refused values leave empty. And what the branches of the values of d all declare holds: a stored
        /// form or a presence that every one declares; that a path holds a string or null, where every one declares a
        /// stored form or a domain of it; and, where every one declares a domain of a path, the union of those domains,
        /// each narrowed as above.
        /// </summary>
        /// <remarks>
        /// The rules read only what the branches declare, against what the scope holds: a consequence that a case would
        /// derive through further rules, such as the claims of another discriminator's branch that a case's own claims
        /// select, is neither derived nor used to drop a value. <see cref="Literal.ContextArgumentTheory"/> opens a
        /// context per value and runs the whole theory there, so its cases derive such consequences.
        /// </remarks>
        /// <param name="ruleset">The ruleset.</param>
        /// <param name="mentioned">Whether the rules reason only by the discriminators the running scope's predicate
        /// mentions (<see cref="Mentioned"/>).</param>
        void DefineCaseRules(Ruleset ruleset, bool mentioned)
        {
            // The discriminator's guards and its domain. A domain of one value is the branch rule's own case, and an
            // empty one is a contradiction.
            Fact[] Cases(Var<string> p, Var<Set<string>> d) =>
                mentioned
                    ? [Discriminator.Of(p), Present.Of(p), Mentioned.Of(p), d.Is(Dom.Of(p))]
                    : [Discriminator.Of(p), Present.Of(p), d.Is(Dom.Of(p))];

            Term<bool> Several(Var<Set<string>> d) => Int64Primitives.Greater.Of(Primitives.SetLength<string>().Of(d), 1L);

            // The values the scope rules out. A case has no document where a path present there, in the scope or by its
            // branch's claims, can hold no value, which is what the lattice rules refute of a present path. A case
            // domain narrows as the scope's domain of its path does; a stored form admits a string or null, and a domain
            // a string only.
            ruleset.DefineRule("a case domain narrows to the path's domain", r =>
            {
                var (p, v, q) = r.Vars<string, string, string>("p", "v", "q");
                var (d, dv, dq) = r.Vars<Set<string>, Set<string>, Set<string>>("d", "dv", "dq");
                r.When(Cases(p, d));
                r.When(dv.Is(CaseDom.Of(p, v, q)), dq.Is(Dom.Of(q)));
                r.Where(Several(d));
                r.Where(Primitives.SetContains<string>().Of(d, v));
                r.Then(CaseDom.Of(p, v, q).Is(Primitives.SetIntersect<string>().Of(dv, dq)));
            });
            ruleset.DefineRule("a disequality narrows a case domain", r =>
            {
                var (p, v, q, w) = r.Vars<string, string, string, string>("p", "v", "q", "w");
                var (d, dv) = r.Vars<Set<string>, Set<string>>("d", "dv");
                r.When(Cases(p, d));
                r.When(dv.Is(CaseDom.Of(p, v, q)), Ne.Of(q, w));
                r.Where(Several(d));
                r.Where(Primitives.SetContains<string>().Of(d, v));
                r.Where(Primitives.SetContains<string>().Of(dv, w));
                r.Then(CaseDom.Of(p, v, q).Is(Primitives.SetRemove<string>().Of(dv, w)));
            });
            foreach (var declared in new[] { false, true })
            {
                var path = declared ? "a path its branch declares present" : "a present path";
                ruleset.DefineRule($"a case with an empty domain of {path} is vacuous", r =>
                {
                    var (p, v, q) = r.Vars<string, string, string>("p", "v", "q");
                    var (d, dv, s) = r.Vars<Set<string>, Set<string>, Set<string>>("d", "dv", "s");
                    r.When(Cases(p, d));
                    r.When(dv.Is(CaseDom.Of(p, v, q)), declared ? s.Is(PresentCases.Of(p, q)) : Present.Of(q));
                    r.Where(Several(d));
                    r.Where(Primitives.SetContains<string>().Of(d, v));
                    r.Where(Primitives.SetIsEmpty<string>().Of(dv));
                    if (declared)
                        r.Where(Primitives.SetContains<string>().Of(s, v));

                    r.Then(Dom.Of(p).Is(Primitives.SetRemove<string>().Of(d, v)));
                });
                ruleset.DefineRule($"a case with a domain of {path} whose types hold no string is vacuous", r =>
                {
                    var (p, v, q) = r.Vars<string, string, string>("p", "v", "q");
                    var (d, dv, t, s) = r.Vars<Set<string>, Set<string>, Set<string>, Set<string>>("d", "dv", "t", "s");
                    r.When(Cases(p, d));
                    r.When(dv.Is(CaseDom.Of(p, v, q)), t.Is(Types.Of(q)), declared ? s.Is(PresentCases.Of(p, q)) : Present.Of(q));
                    r.Where(Several(d));
                    r.Where(Primitives.SetContains<string>().Of(d, v));
                    r.Where(Primitives.SetNotContains<string>().Of(t, "string"));
                    if (declared)
                        r.Where(Primitives.SetContains<string>().Of(s, v));

                    r.Then(Dom.Of(p).Is(Primitives.SetRemove<string>().Of(d, v)));
                });
                ruleset.DefineRule($"cases whose strings the types of {path} exclude are vacuous", r =>
                {
                    var (p, q) = r.Vars<string, string>("p", "q");
                    var (d, s, t, present) = r.Vars<Set<string>, Set<string>, Set<string>, Set<string>>("d", "s", "t", "present");
                    r.When(Cases(p, d));
                    r.When(s.Is(StringCases.Of(p, q)), t.Is(Types.Of(q)), declared ? present.Is(PresentCases.Of(p, q)) : Present.Of(q));
                    r.Where(Several(d));
                    r.Where(Primitives.SetIsEmpty<string>().Of(Primitives.SetIntersect<string>().Of(t, StringOrNull)));
                    r.Then(Dom.Of(p).Is(Primitives.SetDifference<string>().Of(d, declared ? Primitives.SetIntersect<string>().Of(s, present) : s)));
                });
            }

            // A path the branch declares present and the scope leaves no value, by each of the lattice rules' refutations
            // of a present path.
            void DeclaredPresent(string what, Action<RuleBuilder, Var<string>> noValue) =>
                ruleset.DefineRule($"cases that declare present a path {what} are vacuous", r =>
                {
                    var (p, q) = r.Vars<string, string>("p", "q");
                    var (d, s) = r.Vars<Set<string>, Set<string>>("d", "s");
                    r.When(Cases(p, d));
                    r.When(s.Is(PresentCases.Of(p, q)));
                    noValue(r, q);
                    r.Where(Several(d));
                    r.Then(Dom.Of(p).Is(Primitives.SetDifference<string>().Of(d, s)));
                });

            DeclaredPresent("with no possible type", (r, q) =>
            {
                var t = r.Var<Set<string>>("t");
                r.When(t.Is(Types.Of(q)));
                r.Where(Primitives.SetIsEmpty<string>().Of(t));
            });
            DeclaredPresent("whose domain is empty", (r, q) =>
            {
                var dq = r.Var<Set<string>>("dq");
                r.When(dq.Is(Dom.Of(q)));
                r.Where(Primitives.SetIsEmpty<string>().Of(dq));
            });
            DeclaredPresent("whose integer domain is empty", (r, q) =>
            {
                var iq = r.Var<Set<long>>("iq");
                r.When(iq.Is(IDom.Of(q)));
                r.Where(Primitives.SetIsEmpty<long>().Of(iq));
            });
            DeclaredPresent("whose bounds cross", (r, q) =>
            {
                var (l, h) = r.Vars<long, long>("l", "h");
                r.When(l.Is(Lo.Of(q)), h.Is(Hi.Of(q)));
                r.Where(Int64Primitives.Greater.Of(l, h));
            });

            // What the branch of every value of the domain declares.
            ruleset.DefineRule("every case declares a stored form", r =>
            {
                var (p, q, f) = r.Vars<string, string, string>("p", "q", "f");
                var (d, s) = r.Vars<Set<string>, Set<string>>("d", "s");
                r.When(Cases(p, d));
                r.When(s.Is(FormCases.Of(p, q, f)));
                r.Where(Several(d));
                r.Where(Primitives.SetIsSubset<string>().Of(d, s));
                r.Then(Form.Of(q, f));
            });
            ruleset.DefineRule("every case declares presence", r =>
            {
                var (p, q) = r.Vars<string, string>("p", "q");
                var (d, s) = r.Vars<Set<string>, Set<string>>("d", "s");
                r.When(Cases(p, d));
                r.When(s.Is(PresentCases.Of(p, q)));
                r.Where(Several(d));
                r.Where(Primitives.SetIsSubset<string>().Of(d, s));
                r.Then(Present.Of(q));
            });
            ruleset.DefineRule("every case declares a string", r =>
            {
                var (p, q) = r.Vars<string, string>("p", "q");
                var (d, s) = r.Vars<Set<string>, Set<string>>("d", "s");
                r.When(Cases(p, d));
                r.When(s.Is(StringCases.Of(p, q)));
                r.Where(Several(d));
                r.Where(Primitives.SetIsSubset<string>().Of(d, s));
                r.Then(Types.Of(q).Is(StringOrNull));
            });
            ruleset.DefineRule("a case domain joins the union of the cases'", r =>
            {
                var (p, v, q) = r.Vars<string, string, string>("p", "v", "q");
                var (d, dv) = r.Vars<Set<string>, Set<string>>("d", "dv");
                r.When(Cases(p, d));
                r.When(dv.Is(CaseDom.Of(p, v, q)));
                r.Where(Several(d));
                r.Where(Primitives.SetContains<string>().Of(d, v));
                r.Then(DomCases.Of(p, d, q).Is(dv), DomReported.Of(p, d, q).Is(Primitives.SetOf<string>().Of(v)));
            });
            ruleset.DefineRule("every case declares a domain", r =>
            {
                var (p, q) = r.Vars<string, string>("p", "q");
                var (d, reported, u) = r.Vars<Set<string>, Set<string>, Set<string>>("d", "r", "u");
                r.When(Cases(p, d));
                r.When(reported.Is(DomReported.Of(p, d, q)), u.Is(DomCases.Of(p, d, q)));
                r.Where(Several(d));
                r.Where(Primitives.SetIsSubset<string>().Of(d, reported));
                r.Then(Dom.Of(q).Is(u));
            });
        }

    }

}

using System.Globalization;
using System.Linq;

using Eggnog.Tests.Predicates;
using Eggnog.Tests.Predicates.Literal;
using Eggnog.Tests.Support;

using Xunit;

namespace Eggnog.Tests.Rendering
{

    /// <summary>
    /// Rules, facts, terms and schedules render as egglog s-expressions; named primitives render by name, lambdas as
    /// <c>&lt;lambda&gt;</c>.
    /// </summary>
    public sealed class RenderingTests
    {

        /// <summary>
        /// A split rule renders as written, its case tag last; a split step renders with its rules and in-case
        /// schedule.
        /// </summary>
        [Fact]
        public void SplitRulesAndSplitStepsRender()
        {
            var graph = new EGraph();
            var present = graph.DefineRelation<string>("present");
            var dom = graph.DefineFunction<string, ValueSet<string>>("dom", ValueLattice.Intersect<string>());
            var schema = graph.DefineScopeTag("schema");
            var cases = graph.DefineScopeTag("case");
            var horn = graph.DefineRuleset("horn");
            var split = graph.DefineSplit("by kind", s =>
            {
                var (p, d) = s.Vars<string, ValueSet<string>>("p", "d");
                s.When(present.In(schema).Of(p), d.Is(dom.Of(p)));
                var v = s.Cases("v", d);
                s.Assume(dom.Of(p).Is(Primitives.Singleton<string>().Of(v)), present.Of(p));
            }, cases);

            Assert.Equal("(split ((present@schema p) (= d (dom p))) (cases v d) (key p) ((set (dom p) (set-of v)) (present p)) :case-tag case)", split.ToString());
            Assert.Equal(new[] { schema }, split.ReadScopes);
            Assert.Equal("(saturate (run horn) (split (by kind) (run horn)))", Schedule.Saturate(horn, Schedule.Split(split, horn)).ToString());
            var untagged = graph.DefineDomainSplit("plain", dom, [present]);
            Assert.EndsWith("((set (dom k) (set-of v))))", untagged.ToString(), System.StringComparison.Ordinal);
        }

        [Fact]
        public void ARuleRendersAsWritten()
        {
            var c = new ContextArgumentTheory(new EGraph());
            var rule = c.Rewrite.Rules[0];
            Assert.Equal("lower uuid", rule.Name);
            Assert.Equal("(rule ((= e (UuidEq p v)) (form (Query) p \"uuid-lower\")) ((union e (StrEq p v))) :ruleset rewrite)", rule.ToString());
        }

        [Fact]
        public void NamedPrimitivesRenderByName()
        {
            var c = new ContextArgumentTheory(new EGraph());
            var rule = c.Lattice.DefineRule("narrow", r =>
            {
                var (k, p, v, d) = r.Vars<Ctx, string, string, ValueSet<string>>("c", "p", "v", "d");
                r.When(c.Ne.Of(k, p, v), d.Is(c.Dom.Of(k, p)));
                r.Where(Primitives.Contains<string>().Of(d, v));
                r.Then(c.Dom.Of(k, p).Is(Primitives.Remove<string>().Of(d, v)));
            });
            Assert.Equal(
                "(rule ((ne c p v) (= d (dom c p)) (set-contains d v)) ((set (dom c p) (set-remove d v))) :ruleset lattice)",
                rule.ToString());
            Assert.Equal("(set-length (set-diff d s))", Primitives.Count<string>().Of(Primitives.Except<string>().Of(Var<ValueSet<string>>("d"), Var<ValueSet<string>>("s"))).ToString());
            Assert.Equal("(+ n 1)", Primitives.Add.Of(Var<long>("n"), 1L).ToString());
        }

        [Fact]
        public void LambdasRenderAsLambda()
        {
            var t = new NodeTheory();
            var rule = t.Rules.DefineRule("lambdas", r =>
            {
                var (x, y) = r.Vars<long, long>("x", "y");
                r.When(t.Num.Of(x), t.Num.Of(y));
                r.Where(x, y, (a, b) => a < b);
                r.Then(t.Num.Of(Compute.Of(x, y, (a, b) => a + b)));
            });
            Assert.Equal("(rule ((num x) (num y) (guard (<lambda> x y))) ((num (<lambda> x y))) :ruleset rules)", rule.ToString());
        }

        [Fact]
        public void MakesRewritesAndNaiveRulesRender()
        {
            var t = new NodeTheory();
            var make = t.Rules.DefineRule("make", r =>
            {
                var x = r.Var<long>("x");
                r.When(t.Num.Of(x));
                var m = r.Make("m", t.Lit.Of(x));
                r.Then(t.Tag.Of(m));
            });
            Assert.Equal("(rule ((num x)) ((let m (Lit x)) (tag m)) :ruleset rules)", make.ToString());

            var rewrite = t.Rules.DefineRule("commute", r =>
            {
                var (a, b) = r.Vars<Node, Node>("a", "b");
                r.Rewrite(t.Add.Of(a, b), t.Add.Of(b, a));
            });
            Assert.Equal("(rule ((= $rw0 (Add a b))) ((union $rw0 (Add b a))) :ruleset rules)", rewrite.ToString());

            var naive = t.Rules.DefineRule("naive", r =>
            {
                r.Naive();
                r.Then(t.Num.Of(1L), t.Graph.Contradiction.Of());
            });
            Assert.Equal("(rule () ((num 1) ($contradiction)) :ruleset rules :naive)", naive.ToString());
        }

        [Fact]
        public void TermsAndFactsRenderTheirLiterals()
        {
            var t = new NodeTheory();
            var f = t.Graph.DefineFunction<string, ValueSet<string>>("f");
            var a = t.Root.Make(t.A.Of());
            Assert.Equal("(= (f \"kind\") (set-of \"a\" \"b\"))", f.Of("kind").Is(ValueSet.Of("b", "a")).ToString());
            Assert.Equal($"(tag {a.Id})", t.Tag.Of(a).ToString());
            Assert.Equal("(!= 1 2)", ((Term<long>)1L).IsNot(2L).ToString());
            Assert.Equal("(Lit 42)", t.Lit.Of(42L).ToString());
            Assert.Equal("(Add (A) (Lit -1))", t.Add.Of(t.A.Of(), t.Lit.Of(-1L)).ToString());
            Assert.Equal("(f \"x\")", Fact.Exists(f.Of("x")).ToString());
            Assert.Equal("\"text\"", ((Term<string>)"text").ToString());
            Assert.Equal("1.5", ((Term<double>)1.5).ToString());
        }

        [Fact]
        public void SchedulesRenderAsEgglogSchedules()
        {
            var t = new NodeTheory();
            var other = t.Graph.DefineRuleset("other");
            Assert.Equal("(run rules)", ((Schedule)t.Rules).ToString());
            Assert.Equal("(run rules other)", Schedule.Run(t.Rules, other).ToString());
            Assert.Equal("(saturate (run rules) (run other))", Schedule.Saturate(t.Rules, other).ToString());
            Assert.Equal("(repeat 3 (seq (run rules) (run other)))", Schedule.Repeat(3, Schedule.Sequence(t.Rules, other)).ToString());
        }

        /// <summary>
        /// Gets a free-standing variable of another builder, for rendering terms.
        /// </summary>
        static Var<T> Var<T>(string name)
        {
            Var<T>? variable = null;
            new NodeTheory().Rules.DefineRule("holder", r => variable = r.Var<T>(name));
            return variable!;
        }

        [Fact]
        public void UntilAndQueriesRender()
        {
            var t = new NodeTheory();
            Assert.Equal("(saturate (until (run rules) (num 4) (tag (A))))", Schedule.Saturate(Schedule.Until(t.Rules, t.Num.Of(4L), t.Tag.Of(t.A.Of()))).ToString());
            var query = t.Graph.DefineQuery("wrapped", q =>
            {
                var (n, x) = q.Vars<Node, long>("n", "x");
                q.When(n.Is(t.Wrap.Of(t.Lit.Of(x))), t.Num.Of(x));
            });
            Assert.Equal("(query ((= n (Wrap (Lit x))) (num x)) (n x))", query.ToString());
        }

        [Fact]
        public void TableRowsRenderAsSExpressions()
        {
            var t = new NodeTheory();
            var present = t.Graph.DefineRelation<string>("present");
            var dom = t.Graph.DefineFunction<string, ValueSet<string>>("dom", ValueLattice.Intersect<string>());
            var strEq = t.Graph.DefineConstructor<string, string, Node>("StrEq");
            t.Root.Assume(present.Of("kind"), dom.Of("kind").Is(ValueSet.Of("b", "a")));
            var made = t.Root.Make(strEq.Of("kind", "a"));

            Assert.Equal("(present \"kind\")", t.Root.Rows(present)[0].ToString());
            Assert.Equal("(dom \"kind\") = (set-of \"a\" \"b\")", t.Root.Rows(dom)[0].ToString());
            Assert.Equal($"(StrEq \"kind\" \"a\") = {made.Id}", t.Root.Rows(strEq)[0].ToString());
            Assert.Equal("($contradiction)", RowOf(t, t.Graph.Contradiction));
        }

        [Fact]
        public void ScopedReadsAndLicencesRender()
        {
            var c = new Predicates.Scoped.PredicateTheory(new EGraph());
            var tautology = c.Rewrite.Rules[4];
            Assert.Equal("declared tautology", tautology.Name);
            Assert.Equal(
                "(rule ((= s (inset e p)) (present@schema p) (= d (dom@schema p)) (guard (set-subset d s))) ((union e (PTrue))) :ruleset rewrite :erasing schema)",
                tautology.ToString());

            var lower = c.Rewrite.Rules[0];
            Assert.Equal(
                "(rule ((= e (UuidEq p v)) (form p \"uuid-lower\")) ((union e (StrEq p v))) :ruleset rewrite :reads-own-facts \"the guard that proved the form is a conjunct that stays in the predicate\")",
                lower.ToString());

            // Fact.In renders its facts read in the tagged scope; a scoped view renders with its tag.
            Assert.Equal("(present@schema \"kind\") (= (dom@schema \"kind\") (set-of \"a\"))",
                Fact.In(c.Schema, c.Present.Of("kind"), c.Dom.Of("kind").Is(ValueSet.Of("a"))).ToString());
            Assert.Equal("dom@schema", c.Dom.In(c.Schema).ToString());
            Assert.Equal("dom", c.Dom.ToString());
        }

        /// <summary>
        /// Writes the nullary relation and renders its row.
        /// </summary>
        static string RowOf(NodeTheory t, Relation relation)
        {
            t.Root.Assume(relation.Of());
            return t.Root.Rows(relation)[0].ToString();
        }

        /// <summary>
        /// Rows of a nullary function, of the contradiction relation, and of a set-valued function keyed by an enum
        /// render as s-expressions.
        /// </summary>
        [Fact]
        public void RowsOfNullaryTablesSetsAndEnumsRender()
        {
            var t = new NodeTheory();
            t.Graph.Values.RegisterEnum<Shade>();
            var f = t.Graph.DefineFunction<long>("f");
            var dom = t.Graph.DefineFunction<Shade, ValueSet<string>>("dom", ValueLattice.Intersect<string>());
            t.Root.Assume(f.Of().Is(5L), t.Graph.Contradiction.Of(), dom.Of(default(Shade)).Is(ValueSet.Of("b", "a")));

            Assert.Equal("(f) = 5", t.Root.Rows(f).Single().ToString());
            Assert.Equal("($contradiction)", t.Root.Rows(t.Graph.Contradiction).Single().ToString());
            Assert.Equal(string.Create(CultureInfo.InvariantCulture, $"(dom {default(Shade)}) = (set-of \"a\" \"b\")"), t.Root.Rows(dom).Single().ToString());
        }

    }

}

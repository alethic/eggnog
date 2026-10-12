using Eggnog.Tests.Cosmos;
using Eggnog.Tests.Cosmos.Literal;
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

        [Fact]
        public void ARuleRendersAsWritten()
        {
            var c = new PrototypeTheory(new EGraph());
            var rule = c.Rewrite.Rules[0];
            Assert.Equal("lower uuid", rule.Name);
            Assert.Equal("(rule ((= e (UuidEq p v)) (form (Query) p \"uuid-lower\")) ((union e (StrEq p v))) :ruleset rewrite)", rule.ToString());
        }

        [Fact]
        public void NamedPrimitivesRenderByName()
        {
            var c = new PrototypeTheory(new EGraph());
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
            Assert.Equal("(rule ((num x) (num y) (<lambda> x y)) ((num (<lambda> x y))) :ruleset rules)", rule.ToString());
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
            Assert.Equal("(rule () ((num 1) (contradiction)) :ruleset rules :naive)", naive.ToString());
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

    }

}

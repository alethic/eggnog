using System;
using System.Globalization;
using System.Numerics;

using Eggnog.Tests.Support;

using Xunit;

namespace Eggnog.Tests.Rules
{

    /// <summary>
    /// Partial primitives, egglog's <c>-?&gt;</c>, at every place a rule calls one: a call with no result drops the match in
    /// a body let, a filter and a query, and fails the rule in a head and in a split's case assumptions, where egglog
    /// panics. And <c>BigInt</c> values through rules, rendering and extraction.
    /// </summary>
    public sealed class PartialPrimitiveTests
    {

        [Fact]
        public void ABodyLetWithNoResultDropsTheMatch()
        {
            var t = new NodeTheory();
            var hit = t.Graph.DefineRelation<long>("hit");
            t.Rules.DefineRule("hundred over", r =>
            {
                var (x, y) = r.Vars<long, long>("x", "y");
                r.When(t.Num.Of(x), y.Is(Primitives.Divide.Of(100L, x)));
                r.Then(hit.Of(y));
            });
            t.Root.Assume(t.Num.Of(0L), t.Num.Of(10L));

            t.Root.Run(Schedule.Saturate(t.Rules));

            Assert.False(t.Root.IsFaulted);
            Assert.Equal(1, t.Root.CountRows(hit));
            Assert.True(t.Root.Check(hit.Of(10L)));
        }

        [Fact]
        public void AHeadComputationWithNoResultFailsTheRule()
        {
            var t = new NodeTheory();
            var hit = t.Graph.DefineRelation<long>("hit");
            t.Rules.DefineRule("hundred over", r =>
            {
                var x = r.Var<long>("x");
                r.When(t.Num.Of(x));
                r.Then(hit.Of(Primitives.Divide.Of(100L, x)));
            });
            t.Root.Assume(t.Num.Of(0L));

            var e = Assert.Throws<RuleEvaluationException>(() => t.Root.Run(Schedule.Saturate(t.Rules)));

            Assert.Equal("hundred over", e.RuleName);
            Assert.Contains("primitive '/' threw DivideByZeroException", e.Message);
            Assert.Contains("x=0", e.Bindings);
            Assert.IsType<DivideByZeroException>(e.InnerException);
            Assert.True(t.Root.IsFaulted);
        }

        [Fact]
        public void AFilterWithNoResultDropsTheMatch()
        {
            var t = new NodeTheory();
            var hit = t.Graph.DefineRelation<long>("hit");
            var even = Primitive.CreatePartial("even", static (long a, out bool result) =>
            {
                result = a % 2 == 0;
                return a >= 0;
            });
            t.Rules.DefineRule("even", r =>
            {
                var x = r.Var<long>("x");
                r.When(t.Num.Of(x));
                r.Where(even.Of(x));
                r.Then(hit.Of(x));
            });
            t.Root.Assume(t.Num.Of(-2L), t.Num.Of(2L), t.Num.Of(3L));

            t.Root.Run(Schedule.Saturate(t.Rules));

            Assert.False(t.Root.IsFaulted);
            Assert.Equal(1, t.Root.CountRows(hit));
            Assert.True(t.Root.Check(hit.Of(2L)));
        }

        /// <summary>
        /// A partial primitive a user makes with <see cref="Primitive.CreatePartial{T1, TResult}"/> is partial: its
        /// matches with no result are dropped, and its <c>Function</c> throws there. One made with
        /// <see cref="Primitive.Create{T1, TResult}"/> is total.
        /// </summary>
        [Fact]
        public void AUserPartialPrimitiveDropsTheMatchesWithNoResult()
        {
            var graph = new EGraph();
            var text = graph.DefineRelation<string>("text");
            var number = graph.DefineRelation<long>("number");
            var rules = graph.DefineRuleset("rules");
            var parse = Primitive.CreatePartial<string, long>("parse", static (string s, out long n) =>
                long.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out n));
            rules.DefineRule("parse", r =>
            {
                var (s, n) = r.Vars<string, long>("s", "n");
                r.When(text.Of(s), n.Is(parse.Of(s)));
                r.Then(number.Of(n));
            });
            graph.Root.Assume(text.Of("12"), text.Of("twelve"), text.Of("-3"));

            graph.Root.Run(Schedule.Saturate(rules));

            Assert.Equal(1, graph.Root.CountRows(number));
            Assert.True(graph.Root.Check(number.Of(12L)));
            Assert.True(parse.IsPartial);
            Assert.Equal(12, parse.Function("12"));
            Assert.Throws<InvalidOperationException>(() => parse.Function("twelve"));
            Assert.False(Primitive.Create<string, int>("length", static s => s.Length).IsPartial);
        }

        /// <summary>
        /// A <c>double</c> result that is NaN is a value, as in egglog's <c>f64</c>: one NaN, equal to itself.
        /// </summary>
        [Fact]
        public void ANaNResultInABodyIsAValue()
        {
            var graph = new EGraph();
            var pair = graph.DefineRelation<double, double>("pair");
            var difference = graph.DefineRelation<double>("difference");
            var rules = graph.DefineRuleset("rules");
            rules.DefineRule("difference", r =>
            {
                var (a, b, d) = r.Vars<double, double, double>("a", "b", "d");
                r.When(pair.Of(a, b), d.Is(DoublePrimitives.Subtract.Of(a, b)));
                r.Then(difference.Of(d));
            });
            graph.Root.Assume(pair.Of(double.PositiveInfinity, double.PositiveInfinity), pair.Of(3.5, 1.0));

            graph.Root.Run(Schedule.Saturate(rules));

            Assert.False(graph.Root.IsFaulted);
            Assert.Equal(2, graph.Root.CountRows(difference));
            Assert.True(graph.Root.Check(difference.Of(2.5)));
            Assert.True(graph.Root.Check(difference.Of(double.NaN)));
        }

        [Fact]
        public void AQueryDropsTheMatchesWithNoResult()
        {
            var t = new NodeTheory();
            var log = t.Graph.DefineQuery("log", q =>
            {
                var (x, y) = q.Vars<long, long>("x", "y");
                q.When(t.Num.Of(x), y.Is(Primitives.Log2.Of(x)));
            });
            t.Root.Assume(t.Num.Of(0L), t.Num.Of(8L));

            Assert.Equal(1, t.Root.Count(log));
        }

        [Fact]
        public void ACaseAssumptionWithNoResultFailsTheRun()
        {
            var graph = new EGraph();
            var dom = graph.DefineFunction<string, ValueSet<string>>("dom", ValueLattice.Intersect<string>());
            var chosen = graph.DefineRelation<string, string>("chosen");
            var rules = graph.DefineRuleset("rules");
            var never = Primitive.CreatePartial("never", static (string s, out string result) =>
            {
                result = s;
                return false;
            });
            var split = graph.DefineSplit("choose", s =>
            {
                var (p, d) = s.Vars<string, ValueSet<string>>("p", "d");
                s.When(d.Is(dom.Of(p)));
                var v = s.Cases("v", d);
                s.Assume(chosen.Of(p, never.Of(v)));
            });
            graph.Root.Freeze();
            var scope = graph.Root.CreateChild();
            scope.Assume(dom.Of("k").Is(ValueSet.Of("a", "b")));

            var e = Assert.Throws<RuleEvaluationException>(() => scope.Run(Schedule.Split(split, rules)));

            Assert.Contains("primitive 'never' threw InvalidOperationException: Primitive 'never' has no result for these arguments.", e.Message);
            Assert.True(scope.IsFaulted);
        }

        /// <summary>
        /// <c>BigInt</c> is a built-in value type, as in egglog: rules compute with it past <c>i64</c>'s range.
        /// </summary>
        [Fact]
        public void BigIntValuesGoThroughRules()
        {
            var graph = new EGraph();
            var factorial = graph.DefineFunction<long, BigInteger>("factorial");
            var rules = graph.DefineRuleset("rules");
            rules.DefineRule("next", r =>
            {
                var (n, v) = r.Vars<long, BigInteger>("n", "v");
                r.When(factorial.Of(n).Is(v));
                r.Where(Primitives.Less.Of(n, 30L));
                var m = Primitives.Add.Of(n, 1L);
                r.Then(factorial.Of(m).Is(BigIntegerPrimitives.Multiply.Of(v, BigIntegerPrimitives.FromInt64.Of(m))));
            });
            graph.Root.Assume(factorial.Of(0L).Is(BigInteger.One));

            graph.Root.Run(Schedule.Saturate(rules));

            Assert.Equal(BigInteger.Parse("265252859812191058636308480000000", CultureInfo.InvariantCulture), graph.Root.Get(factorial.Of(30L)));
            Assert.Equal("BigInt", graph.Values.TypeOf<BigInteger>().Name);
        }

        /// <summary>
        /// A <c>BigInt</c> renders as egglog reconstructs one, <c>(from-string "…")</c>, in a rule and in an extracted
        /// term.
        /// </summary>
        [Fact]
        public void BigIntRendersAsEgglogsFromString()
        {
            var graph = new EGraph();
            graph.DefineSort<Node>("Node");
            var big = graph.DefineConstructor<BigInteger, Node>("Big");
            var num = graph.DefineRelation<long>("num");
            var mark = graph.DefineRelation<BigInteger>("mark");
            var rules = graph.DefineRuleset("rules");
            var rule = rules.DefineRule("mark", r =>
            {
                var x = r.Var<long>("x");
                r.When(num.Of(x));
                r.Then(mark.Of(new BigInteger(-5)));
            });
            var value = BigInteger.Pow(10, 30);
            var c = graph.Root.Make(big.Of(value));

            Assert.Equal("(rule ((num x)) ((mark (from-string \"-5\"))) :ruleset rules)", rule.ToString());
            Assert.Equal("(Big (from-string \"1000000000000000000000000000000\"))", graph.Root.Extract(c).ToString());
            Assert.Equal("(from-string \"7\")", graph.Values.TypeOf<BigInteger>().Format(7));
        }

        /// <summary>
        /// A <c>BigInt</c> constant renders as <c>(from-string "…")</c> where no column says its type, too: in a
        /// primitive's arguments, a disequality and a bare term, and as a set's elements.
        /// </summary>
        [Fact]
        public void BigIntConstantsRenderAsFromStringWhereverTheyStand()
        {
            var graph = new EGraph();
            var mark = graph.DefineRelation<BigInteger>("mark");
            var rules = graph.DefineRuleset("rules");
            var rule = rules.DefineRule("bump", r =>
            {
                var v = r.Var<BigInteger>("v");
                r.When(mark.Of(v), v.IsNot(new BigInteger(7)));
                r.Where(BigIntegerPrimitives.Less.Of(v, new BigInteger(100)));
                r.Then(mark.Of(BigIntegerPrimitives.Add.Of(v, new BigInteger(5))));
            });

            Assert.Equal("(rule ((mark v) (!= v (from-string \"7\")) (< v (from-string \"100\"))) ((mark (+ v (from-string \"5\")))) :ruleset rules)",
                rule.ToString());
            Assert.Equal("(from-string \"5\")", ((Term<BigInteger>)new BigInteger(5)).ToString());
            Assert.Equal("(set-of (from-string \"1\"))", ((Term<ValueSet<BigInteger>>)ValueSet.Of(BigInteger.One)).ToString());
        }

        /// <summary>
        /// <see cref="BigInteger"/> is built in, so registering it again is refused, saying under which name it is
        /// built in.
        /// </summary>
        [Fact]
        public void BigIntegerCannotBeRegisteredAgain()
        {
            var graph = new EGraph();
            var e = Assert.Throws<ArgumentException>(() => graph.Values.Register<BigInteger>("Big"));
            Assert.Contains("already registered, as 'BigInt'", e.Message);
        }

    }

}

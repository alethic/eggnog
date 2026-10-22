using System;
using System.Linq;

using Eggnog.Tests.Support;

using Xunit;

namespace Eggnog.Tests.Rules
{

    /// <summary>
    /// Primitives of any number of arguments, as egglog's <c>or</c>, string <c>+</c> and <c>set-of</c> are, in rule
    /// bodies and heads; and egglog's <c>ordering-min</c> and <c>ordering-max</c>, which order values by their cells.
    /// </summary>
    public sealed class VariadicPrimitiveTests
    {

        [Fact]
        public void AVariadicPrimitiveTakesAnyNumberOfArgumentsInARule()
        {
            var graph = new EGraph();
            var parts = graph.DefineRelation<string, string, string>("parts");
            var joined = graph.DefineRelation<string>("joined");
            var members = graph.DefineFunction<string, Set<string>>("members", ValueLattice.Union<string>());
            var rules = graph.DefineRuleset("rules");
            var rule = rules.DefineRule("join", r =>
            {
                var (a, b, c) = r.Vars<string, string, string>("a", "b", "c");
                r.When(parts.Of(a, b, c));
                r.Then(joined.Of(StringPrimitives.Concat.Of(a, b, c)), members.Of(a).Is(Primitives.SetOf<string>().Of(a, b, c)));
            });
            graph.Root.Assume(parts.Of("x", "y", "z"));

            graph.Root.Run(Schedule.Saturate(rules));

            Assert.True(graph.Root.Check(joined.Of("xyz")));
            Assert.Equal(Set.Of("x", "y", "z"), graph.Root.Get(members.Of("x")));
            Assert.Equal("(rule ((parts a b c)) ((joined (+ a b c)) (set (members a) (set-of a b c))) :ruleset rules)", rule.ToString());
        }

        [Fact]
        public void OrOfThreeFiltersAsGuard()
        {
            var graph = new EGraph();
            var flags = graph.DefineRelation<bool, bool, bool>("flags");
            var any = graph.DefineRelation<bool, bool, bool>("any");
            var rules = graph.DefineRuleset("rules");
            rules.DefineRule("any", r =>
            {
                var (a, b, c) = r.Vars<bool, bool, bool>("a", "b", "c");
                r.When(flags.Of(a, b, c));
                r.Where(BooleanPrimitives.Or.Of(a, b, c));
                r.Then(any.Of(a, b, c));
            });
            graph.Root.Assume(flags.Of(false, false, false), flags.Of(false, true, false));

            graph.Root.Run(Schedule.Saturate(rules));

            Assert.Equal(1, graph.Root.CountRows(any));
            Assert.True(graph.Root.Check(any.Of(false, true, false)));
        }

        [Fact]
        public void ACallOfNoArgumentsIsItsEmptyValue()
        {
            var graph = new EGraph();
            var num = graph.DefineRelation<long>("num");
            var empty = graph.DefineFunction<long, Set<long>>("empty");
            var rules = graph.DefineRuleset("rules");
            rules.DefineRule("empty", r =>
            {
                var x = r.Var<long>("x");
                r.When(num.Of(x));
                r.Then(empty.Of(x).Is(Primitives.SetOf<long>().Of()));
            });
            graph.Root.Assume(num.Of(1L));

            graph.Root.Run(Schedule.Saturate(rules));

            Assert.Equal(Set<long>.Empty, graph.Root.Get(empty.Of(1L)));
        }

        [Fact]
        public void AUserVariadicPrimitiveMakesOnePrimitivePerArity()
        {
            var sum = Primitive.CreateVariadic<long, long>("sum", static xs => xs.Sum());

            Assert.Equal("sum", sum.Name);
            Assert.Equal(6, sum.Function(new[] { 1L, 2L, 3L }));
            Assert.Same(sum.Call(3), sum.Call(3));
            Assert.NotSame(sum.Call(2), sum.Call(3));
            Assert.Throws<ArgumentNullException>(() => sum.Of(null!));
            Assert.Throws<ArgumentNullException>(() => sum.Of(1L, null!));
        }

        /// <summary>
        /// egglog's <c>ordering-min</c> and <c>ordering-max</c> compare values' representations; a cell is one, an
        /// intern index for a string, so the first string interned is the smaller.
        /// </summary>
        [Fact]
        public void OrderingMinAndMaxCompareCells()
        {
            var graph = new EGraph();
            var pair = graph.DefineRelation<string, string>("pair");
            var least = graph.DefineRelation<string>("least");
            var most = graph.DefineRelation<string>("most");
            var rules = graph.DefineRuleset("rules");
            rules.DefineRule("order", r =>
            {
                var (a, b) = r.Vars<string, string>("a", "b");
                r.When(pair.Of(a, b));
                r.Then(least.Of(Primitives.OrderingMin<string>().Of(a, b)), most.Of(Primitives.OrderingMax<string>().Of(a, b)));
            });
            graph.Root.Assume(pair.Of("z", "a"));

            graph.Root.Run(Schedule.Saturate(rules));

            Assert.True(graph.Root.Check(least.Of("z")));
            Assert.True(graph.Root.Check(most.Of("a")));
            Assert.Throws<InvalidOperationException>(() => Primitives.OrderingMin<string>().Function("a", "b"));
        }

    }

}

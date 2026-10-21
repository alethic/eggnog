using Xunit;

namespace Eggnog.Tests.Rules
{

    /// <summary>
    /// egglog's tests of <c>rust_rule</c>, ported (<c>src/prelude.rs</c> at 5e655cae: <c>rust_api_rust_rule</c>, and
    /// <c>rust_rule</c>'s example): a rule whose right-hand side is host code, here
    /// <see cref="RuleBuilder.ThenCall"/>.
    /// </summary>
    public sealed class RuleCallbackPortedTests
    {

        /// <summary>
        /// rust_api_rust_rule: a callback that writes <c>fib(x + 2)</c> computes <c>fib(20)</c> in ten runs.
        /// </summary>
        [Fact]
        public void RustApiRustRule()
        {
            var (graph, fib) = BuildTestDatabase();
            Assert.False(graph.Root.TryGet(fib.Of(20L), out _));

            var ruleset = graph.DefineRuleset("custom_ruleset");
            ruleset.DefineRule("demo_rule", rule =>
            {
                var (x, f0, f1) = rule.Vars<long, long, long>("x", "f0", "f1");
                rule.When(f0.Is(fib.Of(x)), f1.Is(fib.Of(Primitives.Add.Of(x, 1L))));
                rule.ThenCall(ctx => ctx.Set(fib, [ctx.Get(x) + 2], ctx.Get(f0) + ctx.Get(f1)));
            });

            for (var i = 0; i < 10; i++)
                graph.Root.Run(ruleset);

            Assert.Equal(6765L, graph.Root.Get(fib.Of(20L)));
        }

        /// <summary>
        /// egglog's <c>build_test_database</c>: <c>fib</c>, with a rule that computes it, run ten times.
        /// </summary>
        static (EGraph Graph, Function<long, long> Fib) BuildTestDatabase()
        {
            var graph = new EGraph();
            var fib = graph.DefineFunction<long, long>("fib");
            var rules = graph.DefineRuleset("rules");
            rules.DefineRule("fib", rule =>
            {
                var (x, f0, f1) = rule.Vars<long, long, long>("x", "f0", "f1");
                rule.When(f0.Is(fib.Of(x)), f1.Is(fib.Of(Primitives.Add.Of(x, 1L))));
                rule.Then(fib.Of(Primitives.Add.Of(x, 2L)).Is(Primitives.Add.Of(f0, f1)));
            });
            graph.Root.Assume(fib.Of(0L).Is(0L), fib.Of(1L).Is(1L));
            graph.Root.Run(Schedule.Repeat(10, rules));
            return (graph, fib);
        }

    }

}

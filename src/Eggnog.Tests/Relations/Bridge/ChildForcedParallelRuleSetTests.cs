using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;

using Xunit;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// A child whose rule sets are forced parallel (<see cref="Database.ParallelRuleSetsOverride"/>) runs its rules on
    /// more than one thread at once. Its two rule bodies must meet at a barrier on pool threads, so the class runs in
    /// <see cref="ThreadPoolProbeCollection"/>, apart from the parallel tests that can keep every pool thread busy.
    /// </summary>
    [Collection(ThreadPoolProbeCollection.Name)]
    public class ChildForcedParallelRuleSetTests
    {

        /// <summary>
        /// A child of four threads whose rule sets are forced parallel runs two rules on two threads at once: each rule
        /// matches one edge and calls a primitive that meets the other's call at a barrier and records its thread. A
        /// child that ran the forced rule set serially would leave each call waiting out the barrier's timeout alone;
        /// the soak of <see cref="ConcurrentChildRulesTests"/> relies on the force to create a child's layers
        /// concurrently, and would lose that reach without failing.
        /// </summary>
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void AForcedChildRunsItsRulesOnTwoThreadsAtOnce(bool freezeRoot)
        {
            var g = new ContextGraph(new ParallelHeuristics(4));
            using var barrier = new Barrier(2);
            var met = new ConcurrentQueue<bool>();
            var threads = new ConcurrentQueue<int>();
            var meet = BridgeTestUtil.Register(g.EGraph, (_, _) =>
            {
                met.Enqueue(barrier.SignalAndWait(TimeSpan.FromSeconds(10)));
                threads.Enqueue(Environment.CurrentManagedThreadId);
                return g.Unit.Rep;
            });

            var rules = Enumerable.Range(0, 2).Select(i =>
            {
                var rb = g.EGraph.NewRule("meet-" + i, true);
                rb.QueryTable(g.Edge, [rb.NewVar(ColumnTy.Id), rb.NewVar(ColumnTy.Id), rb.NewVar(g.UnitType)], false);
                rb.CallExternalFunc(meet, [], g.UnitType, static () => "meet");
                return rb.Build();
            }).ToArray();

            var (a, b) = (g.Root.FreshId(), g.Root.FreshId());
            if (freezeRoot)
                g.Root.Freeze();

            var options = freezeRoot ? new ContextOptions { Parallelism = new ParallelHeuristics(4) } : null;
            using var child = g.Root.CreateChild(options);
            Assert.Equal(4, child.Parallelism.DegreeOfParallelism);
            child.Context.Database.ParallelRuleSetsOverride = true;
            g.Assert(child, g.Edge, a, b);
            child.FlushUpdates();
            child.RunRules(rules);

            Assert.Equal([true, true], met.ToArray());
            Assert.Equal(2, threads.Distinct().Count());
        }

    }

}

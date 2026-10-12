using System;
using System.Linq;

using Xunit;

using static Eggnog.Relations.Tests.LayeredTestUtil;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// T12 (colored-contexts spec §17 M10.2): a warm child run allocates nothing per row, probe or frame. The child's
    /// views have two layers, so its nodes, probers and frames are layered: per-layer subsets come from the task's
    /// <see cref="SubsetArrayPool"/>, extra prober pieces and layer hits live in per-position scratch, and multi-layer
    /// nodes from the node pool, all reused once warm (conventions: no allocation per row, match or probe).
    /// </summary>
    [Collection(ExecutorStressCollection.Name)]
    public class LayeredAllocationProbeTests
    {

        /// <summary>
        /// A triangle query over a frozen root's 6000 edges and 1200 edges of the child's own: every atom's rows lie in two
        /// layers. After a warm-up run, a run allocates little per match.
        /// </summary>
        [Theory]
        [InlineData("Gj")]
        [InlineData("PureSize")]
        public void TriangleAllocationsPerMatchInAChild(string strategyName)
        {
            var strategy = Enum.Parse<PlanStrategy>(strategyName);
            var store = new Store(ParallelHeuristics.Serial);
            var e = store.AddTable(Relation(2), name: "e");
            var root = store.Root;
            var ids = Enumerable.Range(0, 400).Select(_ => root.NewId()).ToArray();
            var rng = new Random(3);
            root.Write(state =>
            {
                for (var i = 0; i < 6000; i++)
                    state.StageInsert(e, [ids[rng.Next(ids.Length)], ids[rng.Next(ids.Length)], state.Timestamp]);

                return 0;
            });

            root.Freeze();
            using var child = root.CreateChild();
            child.Write(state =>
            {
                for (var i = 0; i < 1200; i++)
                    state.StageInsert(e, [ids[rng.Next(ids.Length)], ids[rng.Next(ids.Length)], state.Timestamp]);

                return 0;
            });

            long calls = 0;
            var f = root.Database.AddExternalFunction(ExternalFunction.Create((ExecutionState state, ReadOnlySpan<uint> args, out uint result) =>
            {
                calls++;
                result = 0;
                return true;
            }));

            var db = child.Database;
            long Run()
            {
                var rsb = db.NewRuleSet();
                var q = rsb.NewRule();
                q.Strategy = strategy;
                q.NoDecomposition = true;
                var x = q.NewVariable("x");
                var y = q.NewVariable("y");
                var z = q.NewVariable("z");
                q.AddAtom(e, [x, y, QueryEntry.Wildcard()]);
                q.AddAtom(e, [y, z, QueryEntry.Wildcard()]);
                q.AddAtom(e, [z, x, QueryEntry.Wildcard()]);
                var rb = q.Build();
                rb.CallExternal(f, [x, y, z]);
                rb.Build("tri");
                using var rs = rsb.Build();
                var epoch = store.ReadClock();
                var before = GC.GetAllocatedBytesForCurrentThread();
                db.RunRuleSet(rs, ReportLevel.TimeOnly, epoch);
                return GC.GetAllocatedBytesForCurrentThread() - before;
            }

            Run();
            calls = 0;
            var bytes = Run();
            var perMatch = (double)bytes / Math.Max(1, calls);
            Assert.True(calls > 1000, $"{strategyName}: only {calls} matches");
            Assert.True(child.GetView(e).Layers.Count(l => l is not null) == 2, "the child's view of e should have two layers");
            Assert.True(perMatch < 64, $"{strategyName}: {bytes} bytes for {calls} matches = {perMatch:0.0} bytes per match");
        }

    }

}

using System;
using System.Linq;

using Xunit;

using static Eggnog.Relations.Tests.QueryTestUtil;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// Allocations of the serial executor once its pools are warm: no allocation per row, match or probe on hot paths.
    /// </summary>
    [Collection(ExecutorStressCollection.Name)]
    public class ExecutorAllocationTests
    {

        /// <summary>
        /// A recycled trie node (the per-frame nodes of sparse refinements come from <see cref="TrieNodePool"/>) that
        /// is probed through a per-node column index allocates nothing once warm. <see cref="TrieNode.ResetCaches"/>
        /// keeps the disposed index as a spare, and the next build refills it in place; dropping it would make every
        /// build allocate a whole new <see cref="ColumnIndex"/>: the object, its shard array, a projection array,
        /// free-list heads and a short arena.
        /// </summary>
        [Fact]
        public void PerNodeColumnIndexOnARecycledNodeDoesNotAllocate()
        {
            var table = TableTestUtil.FillTable(Enumerable.Range(0, 400).Select(i => new[] { (uint)i, (uint)(i % 7) }), 1, null, ClosureMerge.TakeNew);
            var pool = new TrieNodePool(false);
            for (var i = 0; i < 100; i++)
                Cycle(pool, table);

            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var i = 0; i < 1000; i++)
                Cycle(pool, table);

            var perCycle = (GC.GetAllocatedBytesForCurrentThread() - before) / 1000.0;
            Assert.True(perCycle < 8, $"{perCycle:0.0} bytes per node probed through its column index");

            static void Cycle(TrieNodePool pool, Table table)
            {
                // Eleven rows: above the sparse index's eight, below half the table, so get_index builds a
                // DynamicColumn.
                var node = pool.Rent(Subset.Dense(40, 51));
                node.GetCachedIndex(new ColumnId(1), table);
                node.Release(pool);
            }
        }

        /// <summary>
        /// The same at the level of a rule: a triangle query over 6000 edges, run serially after a warm-up run,
        /// allocates little per match. The costs to avoid are hundreds of bytes per match, from per-node column indexes
        /// (ColumnIndexShard[], ColumnIndex, int[1] projections, free-list heads), from a fresh join (node pool, frame
        /// buffers) per run, and from small subset buffers beyond the shared array pool's per-length capacity.
        /// </summary>
        [Theory]
        [InlineData("Gj")]
        [InlineData("PureSize")]
        public void TriangleAllocationsPerMatch(string strategyName)
        {
            var strategy = Enum.Parse<PlanStrategy>(strategyName);
            var db = ExecutionTestUtil.NewDatabase("Serial");
            var e = db.AddTable(Strict(2, 2));
            var rng = new Random(3);
            DatabaseTestUtil.Stage(db, e, Enumerable.Range(0, 6000).Select(_ => new[] { (uint)rng.Next(400), (uint)rng.Next(400) }).ToArray());
            db.MergeAll();
            long calls = 0;
            var f = db.AddExternalFunction(ExternalFunction.Create((ExecutionState state, ReadOnlySpan<uint> args, out uint result) =>
            {
                calls++;
                result = 0;
                return true;
            }));

            long Run()
            {
                var rsb = db.NewRuleSet();
                var q = rsb.NewRule();
                q.Strategy = strategy;
                q.NoDecomposition = true;
                var x = q.NewVariable("x");
                var y = q.NewVariable("y");
                var z = q.NewVariable("z");
                q.AddAtom(e, [x, y]);
                q.AddAtom(e, [y, z]);
                q.AddAtom(e, [z, x]);
                var rb = q.Build();
                rb.CallExternal(f, [x, y, z]);
                rb.Build("tri");
                using var rs = rsb.Build();
                var before = GC.GetAllocatedBytesForCurrentThread();
                db.RunRuleSet(rs);
                return GC.GetAllocatedBytesForCurrentThread() - before;
            }

            Run();
            calls = 0;
            var bytes = Run();
            var perMatch = (double)bytes / Math.Max(1, calls);
            Assert.True(perMatch < 64, $"{strategyName}: {bytes} bytes for {calls} matches = {perMatch:0.0} bytes per match");
        }

    }

}

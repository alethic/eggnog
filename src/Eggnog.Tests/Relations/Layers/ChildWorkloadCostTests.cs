using System;
using System.Linq;

using Xunit;

using static Eggnog.Relations.Tests.LayeredTestUtil;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// A child costs in proportion to what it touches: what a child of a frozen root allocates and probes for a fixed
    /// amount of work (creation, writes that add and hide, lookups, views, a freeze, a grandchild) does not grow with
    /// the root's row count.
    /// </summary>
    [Collection(AllocationProbeCollection.Name)]
    public class ChildWorkloadCostTests
    {

        /// <summary>
        /// Runs the fixed child workload over a frozen root of <paramref name="rootRows"/> rows and returns the bytes
        /// it allocated and the child's lookup probes.
        /// </summary>
        static (long Bytes, long Probes) ChildWorkload(int rootRows)
        {
            var store = new Store(ParallelHeuristics.Serial);
            var f = store.AddTable(MaxFunction(1), name: "f");
            var root = store.Root;
            var ids = new uint[rootRows];
            for (var i = 0; i < rootRows; i++)
                ids[i] = root.NewId();

            root.Write(state =>
            {
                foreach (var id in ids)
                    state.StageInsert(f, [id, 1, state.Timestamp]);

                return 0;
            });
            root.Freeze();

            // Warm the code paths once, on a throwaway child.
            Run(store, f, ids);
            GC.Collect();
            var before = GC.GetAllocatedBytesForCurrentThread();
            var probes = Run(store, f, ids);
            return (GC.GetAllocatedBytesForCurrentThread() - before, probes);
        }

        /// <summary>
        /// The fixed workload of one child.
        /// </summary>
        static long Run(Store store, TableId f, uint[] ids)
        {
            var child = store.Root.CreateChild();
            child.Write(state =>
            {
                for (var i = 0; i < 25; i++)
                    state.StageInsert(f, [ids[i], 5, state.Timestamp]);

                for (var i = 0; i < 25; i++)
                    state.StageInsert(f, [child.NewId(), 5, state.Timestamp]);

                return 0;
            });

            var row = new uint[3];
            for (var i = 0; i < 100; i++)
                child.TryGetRow(f, [ids[i % 50]], row);

            _ = child.GetView(f).VisibleCount;
            child.Freeze();
            var grandchild = child.CreateChild();
            grandchild.Write(state =>
            {
                for (var i = 25; i < 35; i++)
                    state.StageInsert(f, [ids[i], 7, state.Timestamp]);

                return 0;
            });

            _ = grandchild.GetView(f).VisibleCount;
            var probes = child.Tables!.Statistics.LookupProbes + grandchild.Tables!.Statistics.LookupProbes;
            grandchild.Dispose();
            child.Dispose();
            return probes;
        }

        [Fact]
        public void ChildCostDoesNotGrowWithTheRoot()
        {
            var small = ChildWorkload(2_000);
            var large = ChildWorkload(200_000);
            Assert.Equal(small.Probes, large.Probes);
            Assert.True(large.Bytes <= small.Bytes * 3 / 2, $"child workload allocated {small.Bytes} bytes over a 2k-row root and {large.Bytes} over a 200k-row root");
        }

    }

}

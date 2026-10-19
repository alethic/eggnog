using System;

using Xunit;

using static Eggnog.Relations.Tests.LayeredTestUtil;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// What a light child costs, since a child pays in proportion to what it touches: the bytes a bare child and an
    /// empty layer allocate, measured on this thread after a warm-up, stay under the bounds the lazy pending queues,
    /// lazy notification flags and lazy index-catalog gates bring them to; and the root's tables keep their eager
    /// queues.
    /// </summary>
    /// <remarks>
    /// The bounds have headroom over the Release figures (about 2.5 KB and 1.1 KB, <c>--context-footprint</c> of the
    /// benchmark app) for the DEBUG-only bookkeeping of the test build. Without the lazy queues, flags and gates the
    /// same workloads allocate about 4.7 KB and 2.8 KB, mostly two
    /// <see cref="System.Collections.Concurrent.ConcurrentQueue{T}"/> segments per pending state.
    /// </remarks>
    [Trait("Category", "Performance")]
    [Collection(AllocationProbeCollection.Name)]
    public class ChildFootprintTests
    {

        const int Warmup = 50;
        const int Repetitions = 100;

        /// <summary>
        /// The windows of <see cref="Repetitions"/> calls <see cref="Measure"/> takes.
        /// </summary>
        const int Windows = 5;

        /// <summary>
        /// Runs <paramref name="action"/> to warm the pools, then returns the bytes one call allocates on this thread:
        /// the least, over <see cref="Windows"/> windows, of a window's bytes divided by its <see cref="Repetitions"/>
        /// calls.
        /// </summary>
        /// <remarks>
        /// A child rents its storage from the shared array pool, which the runtime trims after a full collection under
        /// memory pressure, so on a loaded machine one window can rent fresh arrays: a single window of 500 calls once
        /// read 1,760 B for an empty layer on an osx-x64 runner, against about 1,190 B elsewhere (Debug, net10.0). An
        /// allocation of every call shows in every window, so the least still catches it.
        /// </remarks>
        static double Measure(Action action)
        {
            for (var i = 0; i < Warmup; i++)
                action();

            var least = long.MaxValue;
            for (var window = 0; window < Windows; window++)
            {
                var before = GC.GetAllocatedBytesForCurrentThread();
                for (var i = 0; i < Repetitions; i++)
                    action();

                least = Math.Min(least, GC.GetAllocatedBytesForCurrentThread() - before);
            }

            return least / (double)Repetitions;
        }

        static (Store Store, TableId F) FrozenRoot()
        {
            var store = new Store(ParallelHeuristics.Serial);
            var f = store.AddTable(MaxFunction(1), name: "f");
            store.AddTable(Relation(2), name: "r");
            var root = store.Root;
            var ids = new uint[64];
            for (var i = 0; i < ids.Length; i++)
                ids[i] = root.NewId();

            root.Write(state =>
            {
                foreach (var id in ids)
                    state.StageInsert(f, [id, 1, state.Timestamp]);

                return 0;
            });
            root.Freeze();
            return (store, f);
        }

        /// <summary>
        /// Creating and disposing a child of a frozen root allocates under 3,200 bytes, and creating an empty layer in
        /// it under 1,600 more.
        /// </summary>
        [Fact]
        public void ABareChildAndAnEmptyLayerStayLight()
        {
            var (store, f) = FrozenRoot();
            var bare = Measure(() => store.Root.CreateChild().Dispose());
            var layered = Measure(() =>
            {
                var child = store.Root.CreateChild();
                child.Tables!.GetOrCreateLayer(f);
                child.Dispose();
            });

            Assert.True(bare < 3_200, $"a bare child allocated {bare:N0} B");
            Assert.True(layered - bare < 1_600, $"an empty layer allocated {layered - bare:N0} B");
        }

        /// <summary>
        /// A child's layer and union-find table create their pending queues only when they stage; a root's are eager.
        /// </summary>
        [Fact]
        public void OnlyChildTablesDeferTheirQueues()
        {
            var (store, f) = FrozenRoot();
            using var child = store.Root.CreateChild();
            var layer = child.Tables!.GetOrCreateLayer(f).Own;
            var other = store.Root.CreateChild();
            var second = other.Tables!.GetOrCreateLayer(f).Own;
            Assert.Same(layer.Pending.RowQueue(0), second.Pending.RowQueue(0));
            other.Dispose();

            var root = (SortedWritesTable)store.Root.Database.GetTable(f);
            Assert.NotSame(root.Pending.RowQueue(0), layer.Pending.RowQueue(0));

            // The first write creates the child's queue and lands.
            var shared = layer.Pending.RowQueue(0);
            Insert(child, f, 5, 3, 0);
            Assert.NotSame(shared, layer.Pending.RowQueue(0));
            Assert.True(shared.IsEmpty);
            Assert.Equal(1L, child.Database.TotalSizeEstimate);
        }

    }

}

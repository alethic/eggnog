using System;

using Xunit;

using static Eggnog.Relations.Tests.LayeredTestUtil;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// What a light child costs (colored-contexts spec §14.2, C14, §21.9, M10.4): the bytes a bare child and an empty
    /// layer allocate, measured on this thread after a warm-up, stay under the bounds the lazy pending queues, lazy
    /// notification flags and lazy index-catalog gates bring them to; and the root's tables keep their eager queues.
    /// </summary>
    /// <remarks>
    /// The bounds have headroom over the Release figures (about 2.5 KB and 1.1 KB, <c>--context-footprint</c> of the
    /// benchmark app) for the DEBUG-only bookkeeping of the test build. Before M10.4 the same workloads allocated about
    /// 4.7 KB and 2.8 KB, mostly two <see cref="System.Collections.Concurrent.ConcurrentQueue{T}"/> segments per pending
    /// state.
    /// </remarks>
    [Trait("Category", "Performance")]
    [Collection(AllocationProbeCollection.Name)]
    public class ChildFootprintTests
    {

        const int Warmup = 50;
        const int Repetitions = 500;

        static double Measure(Action action)
        {
            for (var i = 0; i < Warmup; i++)
                action();

            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var i = 0; i < Repetitions; i++)
                action();

            return (GC.GetAllocatedBytesForCurrentThread() - before) / (double)Repetitions;
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

using System;

using Xunit;

using static Eggnog.Relations.Tests.LayeredTestUtil;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// A child pays in proportion to what it touches: the same child workload over roots of 20k and 200k rows examines
    /// and copies exactly the same ancestor rows, and allocates within 1.5× as much, also for the first child of a
    /// writable root.
    /// </summary>
    /// <remarks>
    /// The root holds <c>f(x) → v</c> for every id and <c>r(x, x+1)</c> for every id; the child unions the first 200
    /// ids in pairs (100 unions), which makes 100 of them its own losers, and rebuilds. The rebuild then finds, through
    /// the frozen root's rebuild indexes, exactly the rows naming those losers (one <c>f</c> row and two <c>r</c> rows
    /// each), whatever the root's size, and never scans. The catch-up case appends a fixed number of rows to a root
    /// that is not frozen.
    /// </remarks>
    [Trait("Category", "Performance")]
    [Collection(AllocationProbeCollection.Name)]
    public class PerChildCostTests
    {

        readonly ITestOutputHelper output;

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="output">The test output.</param>
        public PerChildCostTests(ITestOutputHelper output)
        {
            this.output = output;
        }

        /// <summary>
        /// The number of unions the child makes.
        /// </summary>
        const int Unions = 100;

        /// <summary>
        /// Builds and freezes a root of <paramref name="rows"/> ids, with an <c>f</c> and an <c>r</c> row for each.
        /// </summary>
        static (Store Store, TableId F, TableId R, uint[] Ids) FrozenRoot(int rows)
        {
            var store = new Store(ParallelHeuristics.Serial);
            var f = store.AddTable(MaxFunction(1), name: "f");
            var r = store.AddTable(Relation(2), name: "r");
            var root = store.Root;
            var ids = new uint[rows];
            for (var i = 0; i < rows; i++)
                ids[i] = root.NewId();

            root.Write(state =>
            {
                for (var i = 0; i < ids.Length; i++)
                {
                    state.StageInsert(f, [ids[i], (uint)(i % 7), state.Timestamp]);
                    state.StageInsert(r, [ids[i], ids[(i + 1) % ids.Length], state.Timestamp]);
                }

                return 0;
            });
            root.Freeze();
            return (store, f, r, ids);
        }

        /// <summary>
        /// The child workload: creation, 100 unions, a rebuild, a few reads, disposal. Returns the child's statistics.
        /// </summary>
        static LayerStatistics ChildWorkload(Store store, TableId f, uint[] ids)
        {
            var child = store.Root.CreateChild();
            child.Write((ids, store.UnionFindTable), static (state, args) =>
            {
                Span<uint> row = stackalloc uint[3];
                for (var i = 0; i < Unions; i++)
                {
                    row[0] = args.ids[2 * i];
                    row[1] = args.ids[2 * i + 1];
                    row[2] = state.Timestamp;
                    state.StageInsert(args.UnionFindTable, row);
                }

                return 0;
            });
            child.Rebuild();
            var cells = new uint[3];
            for (var i = 0; i < 2 * Unions; i++)
                child.TryGetRow(f, [child.Find(ids[i])], cells);

            var statistics = child.Tables!.Statistics;
            child.Dispose();
            return statistics;
        }

        /// <summary>
        /// Runs the workload over a frozen root of <paramref name="rows"/> rows: once to warm up, once measured.
        /// </summary>
        static (LayerStatistics Statistics, long Bytes) Measure(int rows)
        {
            var (store, f, _, ids) = FrozenRoot(rows);
            ChildWorkload(store, f, ids);
            GC.Collect();
            var before = GC.GetAllocatedBytesForCurrentThread();
            var statistics = ChildWorkload(store, f, ids);
            return (statistics, GC.GetAllocatedBytesForCurrentThread() - before);
        }

        /// <summary>
        /// One hundred unions and a rebuild in a child of a 20k-row and of a 200k-row frozen root: the rebuild examines
        /// and copies the same 300 rows (one f and two r rows per loser), the catch-up reads nothing, and the larger
        /// root costs at most 1.5 times the bytes.
        /// </summary>
        [Fact]
        public void AChildsRebuildCostDoesNotGrowWithTheRoot()
        {
            var small = Measure(20_000);
            var large = Measure(200_000);

            // The rebuild examines and copies the rows naming the losers: one f row and two r rows per loser.
            Assert.Equal(3 * Unions, small.Statistics.AncestorRowsExamined);
            Assert.Equal(small.Statistics.AncestorRowsExamined, large.Statistics.AncestorRowsExamined);
            Assert.Equal(small.Statistics.RowsCopied, large.Statistics.RowsCopied);
            Assert.Equal(small.Statistics.CatchUpRowsExamined, large.Statistics.CatchUpRowsExamined);
            Assert.Equal(0, large.Statistics.CatchUpRowsExamined);
            Assert.Equal(0, large.Statistics.RestampedCopies);
            Assert.True(large.Bytes <= small.Bytes * 3 / 2, $"the child workload allocated {small.Bytes} bytes over a 20k-row root and {large.Bytes} over a 200k-row root");
        }

        /// <summary>
        /// The same workload under a root that is not frozen, with no warm-up child: the first child of a 20k-row and
        /// of a 200k-row writable root examines and copies the same rows and allocates within 1.5 times as much,
        /// measured from after its creation. Building the root's rebuild indexes is the root's work when it creates the
        /// child, so the child's rebuild never builds a whole ancestor index on its path.
        /// </summary>
        [Fact]
        public void TheFirstChildOfAWritableRootDoesNotPayForTheRootsSize()
        {
            static (LayerStatistics Statistics, long Bytes) Run(int rows)
            {
                var store = new Store(ParallelHeuristics.Serial);
                var f = store.AddTable(MaxFunction(1), name: "f");
                var r = store.AddTable(Relation(2), name: "r");
                var root = store.Root;
                var ids = new uint[rows];
                for (var i = 0; i < rows; i++)
                    ids[i] = root.NewId();

                root.Write(state =>
                {
                    for (var i = 0; i < ids.Length; i++)
                    {
                        state.StageInsert(f, [ids[i], (uint)(i % 7), state.Timestamp]);
                        state.StageInsert(r, [ids[i], ids[(i + 1) % ids.Length], state.Timestamp]);
                    }

                    return 0;
                });

                var child = root.CreateChild();
                GC.Collect();
                var before = GC.GetAllocatedBytesForCurrentThread();
                child.Write((ids, store.UnionFindTable), static (state, args) =>
                {
                    Span<uint> row = stackalloc uint[3];
                    for (var i = 0; i < Unions; i++)
                    {
                        row[0] = args.ids[2 * i];
                        row[1] = args.ids[2 * i + 1];
                        row[2] = state.Timestamp;
                        state.StageInsert(args.UnionFindTable, row);
                    }

                    return 0;
                });
                child.Rebuild();
                var bytes = GC.GetAllocatedBytesForCurrentThread() - before;
                return (child.Tables!.Statistics, bytes);
            }

            var small = Run(20_000);
            var large = Run(200_000);
            Assert.Equal(3 * Unions, small.Statistics.AncestorRowsExamined);
            Assert.Equal(small.Statistics.AncestorRowsExamined, large.Statistics.AncestorRowsExamined);
            Assert.Equal(small.Statistics.RowsCopied, large.Statistics.RowsCopied);
            Assert.Equal(0, large.Statistics.CatchUpRowsExamined);
            Assert.True(large.Bytes <= small.Bytes * 3 / 2, $"the first child of a writable root allocated {small.Bytes} bytes over a 20k-row root and {large.Bytes} over a 200k-row root");
        }

        /// <summary>
        /// A root that is not frozen appends 50 rows after its child exists: the child's catch-up examines exactly
        /// those 50, whatever the root's size, and copies the one whose key it holds.
        /// </summary>
        [Fact]
        public void ACatchUpExaminesOnlyTheAncestorsAppends()
        {
            static LayerStatistics Run(int rows)
            {
                var store = new Store(ParallelHeuristics.Serial);
                var f = store.AddTable(MaxFunction(1), name: "f");
                var root = store.Root;
                var ids = new uint[rows];
                for (var i = 0; i < rows; i++)
                    ids[i] = root.NewId();

                root.Write(state =>
                {
                    foreach (var id in ids)
                        state.StageInsert(f, [id, 1, state.Timestamp]);

                    return 0;
                });

                var child = root.CreateChild();
                Insert(child, f, ids[0], 9, 0);
                root.Write(state =>
                {
                    for (var i = 0; i < 50; i++)
                        state.StageInsert(f, [ids[i], 5, state.Timestamp]);

                    return 0;
                });

                child.Rebuild();
                Assert.Equal(9u, Row(child, f, ids[0])![1]);
                Assert.Equal(5u, Row(child, f, ids[1])![1]);
                return child.Tables!.Statistics;
            }

            var small = Run(20_000);
            var large = Run(200_000);
            Assert.Equal(50, small.CatchUpRowsExamined);
            Assert.Equal(small.CatchUpRowsExamined, large.CatchUpRowsExamined);
            Assert.Equal(small.RowsCopied, large.RowsCopied);
            Assert.Equal(1, large.RowsCopied);
        }

        static (Store Store, TableId F, uint[] Ids) WritableRoot(int rows)
        {
            var store = new Store(ParallelHeuristics.Serial);
            var f = store.AddTable(MaxFunction(1), name: "f");
            var root = store.Root;
            var ids = new uint[rows];
            for (var i = 0; i < rows; i++)
                ids[i] = root.NewId();

            root.Write(state =>
            {
                for (var i = 0; i < ids.Length; i++)
                    state.StageInsert(f, [ids[i], (uint)(i % 7), state.Timestamp]);

                return 0;
            });
            return (store, f, ids);
        }

        /// <summary>
        /// Bytes and time of one child of a writable root: one union of two root ids, then a rebuild (which probes the
        /// root's rebuild index). Measured from after CreateChild, so the root's settle is excluded.
        /// </summary>
        static (long Bytes, double Ms, long Examined) ChildUnion(Store store, uint a, uint b)
        {
            var child = store.Root.CreateChild();
            GC.Collect();
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var before = GC.GetAllocatedBytesForCurrentThread();
            child.Union(a, b);
            child.Rebuild();
            var bytes = GC.GetAllocatedBytesForCurrentThread() - before;
            watch.Stop();
            var examined = child.Tables!.Statistics.AncestorRowsExamined;
            child.Dispose();
            return (bytes, watch.Elapsed.TotalMilliseconds, examined);
        }

        /// <summary>
        /// The first single-union child of a writable root examines as many ancestor rows over 20,000 rows as over
        /// 200,000, and allocates within 1.5 times as much, the root's rebuild index included; the second child is
        /// reported beside it.
        /// </summary>
        [Fact]
        public void FirstChildOfAWritableRootPaysForTheRootsRebuildIndex()
        {
            var (smallStore, _, smallIds) = WritableRoot(20_000);
            var (largeStore, _, largeIds) = WritableRoot(200_000);

            var small1 = ChildUnion(smallStore, smallIds[0], smallIds[1]);
            var large1 = ChildUnion(largeStore, largeIds[0], largeIds[1]);
            var small2 = ChildUnion(smallStore, smallIds[2], smallIds[3]);
            var large2 = ChildUnion(largeStore, largeIds[2], largeIds[3]);
            output.WriteLine($"first child:  20k {small1.Bytes} B {small1.Ms:F2} ms examined {small1.Examined}; 200k {large1.Bytes} B {large1.Ms:F2} ms examined {large1.Examined}");
            output.WriteLine($"second child: 20k {small2.Bytes} B {small2.Ms:F2} ms examined {small2.Examined}; 200k {large2.Bytes} B {large2.Ms:F2} ms examined {large2.Examined}");
            Assert.Equal(small1.Examined, large1.Examined);
            Assert.True(large1.Bytes <= small1.Bytes * 3 / 2, $"first child: {small1.Bytes} B over 20k rows, {large1.Bytes} B over 200k rows");
        }

        /// <summary>
        /// With the root's rebuild index built beforehand, the first child of a writable root allocates within 1.5
        /// times as much over 200,000 rows as over 20,000.
        /// </summary>
        [Fact]
        public void WithTheRootsRebuildIndexPrebuiltTheFirstChildIsFlat()
        {
            var (smallStore, sf, smallIds) = WritableRoot(20_000);
            var (largeStore, lf, largeIds) = WritableRoot(200_000);
            Assert.Null(typeof(SortedWritesTable).GetField("rebuildIndex", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(largeStore.Schema[lf]!.Root));
            smallStore.Schema[sf]!.Root.SharedRebuildIndex();
            largeStore.Schema[lf]!.Root.SharedRebuildIndex();
            var small1 = ChildUnion(smallStore, smallIds[0], smallIds[1]);
            var large1 = ChildUnion(largeStore, largeIds[0], largeIds[1]);
            output.WriteLine($"first child (index prebuilt): 20k {small1.Bytes} B {small1.Ms:F2} ms; 200k {large1.Bytes} B {large1.Ms:F2} ms");
            Assert.True(large1.Bytes <= small1.Bytes * 3 / 2, $"first child: {small1.Bytes} B over 20k rows, {large1.Bytes} B over 200k rows");
        }

    }

}

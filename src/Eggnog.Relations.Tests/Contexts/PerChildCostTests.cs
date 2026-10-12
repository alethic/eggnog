using System;

using Xunit;

using static Eggnog.Relations.Tests.LayeredTestUtil;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// A child pays in proportion to what it touches (colored-contexts spec §17 M9.4 gate, §14.2, L6, B4): the same child
    /// workload over roots of 20k and 200k rows examines and copies exactly the same ancestor rows, and allocates within
    /// 1.5× as much.
    /// </summary>
    /// <remarks>
    /// The root holds <c>f(x) → v</c> for every id and <c>r(x, x+1)</c> for every id; the child unions the first 200 ids
    /// in pairs (100 unions), which makes 100 of them its own losers, and rebuilds. Step 4 then finds, through the frozen
    /// root's rebuild indexes, exactly the rows naming those losers (one <c>f</c> row and two <c>r</c> rows each), whatever
    /// the root's size, and never scans. The catch-up case appends a fixed number of rows to a root that is not frozen.
    /// </remarks>
    [Trait("Category", "Performance")]
    public class PerChildCostTests
    {

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
        /// One hundred unions and a rebuild in a child of a 20k-row and of a 200k-row frozen root: step 4 examines and
        /// copies the same 300 rows (one f and two r rows per loser), the catch-up reads nothing, and the larger root
        /// costs at most 1.5 times the bytes.
        /// </summary>
        [Fact]
        public void AChildsRebuildCostDoesNotGrowWithTheRoot()
        {
            var small = Measure(20_000);
            var large = Measure(200_000);

            // Step 4 examines and copies the rows naming the losers: one f row and two r rows per loser.
            Assert.Equal(3 * Unions, small.Statistics.AncestorRowsExamined);
            Assert.Equal(small.Statistics.AncestorRowsExamined, large.Statistics.AncestorRowsExamined);
            Assert.Equal(small.Statistics.RowsCopied, large.Statistics.RowsCopied);
            Assert.Equal(small.Statistics.CatchUpRowsExamined, large.Statistics.CatchUpRowsExamined);
            Assert.Equal(0, large.Statistics.CatchUpRowsExamined);
            Assert.Equal(0, large.Statistics.RestampedCopies);
            Assert.True(large.Bytes <= small.Bytes * 3 / 2, $"the child workload allocated {small.Bytes} bytes over a 20k-row root and {large.Bytes} over a 200k-row root");
        }

        /// <summary>
        /// The same workload under a root that is not frozen, with no warm-up child: the first child of a 20k-row and of a
        /// 200k-row writable root examines and copies the same rows and allocates within 1.5 times as much, measured from
        /// after its creation. Building the root's rebuild indexes is the root's work when it creates the child (§1 item
        /// 11), so step 4 never builds a whole ancestor index on the child's path (§14.2).
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

    }

}

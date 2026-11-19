using System;
using System.Linq;

using Xunit;

using static Eggnog.Relations.Tests.LayeredTestUtil;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// A child pays in proportion to what it touches: the same child workload over roots of 20k and 200k rows examines
    /// and copies exactly the same ancestor rows, and allocates within 1.5× as much, also for the first child of a
    /// writable root and on the copy-volume benchmark's workload. Open children leave the root's write path as it is:
    /// the root's tables keep their eager pending queues, and its publication path allocates what an eager state does.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The root holds <c>f(x) → v</c> for every id and <c>r(x, x+1)</c> for every id; the child unions the first 200
    /// ids in pairs (100 unions), which makes 100 of them its own losers, and rebuilds. The rebuild then finds, through
    /// the frozen root's rebuild indexes, exactly the rows naming those losers (one <c>f</c> row and two <c>r</c> rows
    /// each), whatever the root's size, and never scans. The catch-up case appends a fixed number of rows to a root
    /// that is not frozen.
    /// </para>
    /// <para>
    /// The copy-volume root (<see cref="CopyVolumeRoot"/>) is the copy-volume benchmark's: one <c>f</c> row and one
    /// <c>r(x, h(x))</c> row per id, with heavy-tailed hubs, so a union whose loser is a hub copies many rows.
    /// </para>
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

        /// <summary>
        /// Gets the fewest bytes per repetition that <paramref name="action"/> allocates on this thread over three
        /// windows of <paramref name="repetitions"/> runs, after <paramref name="warmup"/> runs.
        /// </summary>
        static double Measure(Action action, int warmup, int repetitions)
        {
            for (var i = 0; i < warmup; i++)
                action();

            var best = double.MaxValue;
            for (var window = 0; window < 3; window++)
            {
                var before = GC.GetAllocatedBytesForCurrentThread();
                for (var i = 0; i < repetitions; i++)
                    action();

                best = Math.Min(best, (GC.GetAllocatedBytesForCurrentThread() - before) / (double)repetitions);
            }

            return best;
        }

        /// <summary>
        /// The root's tables keep their own (eager) pending queues while children are open and the root is written;
        /// only the children's layers share the empty queue until they stage.
        /// </summary>
        [Fact]
        public void TheRootKeepsEagerQueuesWhileChildrenAreOpen()
        {
            var g = new ContextGraph();
            var ids = Enumerable.Range(0, 4).Select(_ => g.Root.FreshId()).ToArray();
            g.Assert(g.Root, g.Edge, ids[0], ids[1]);
            g.Root.FlushUpdates();

            using var one = g.Root.CreateChild();
            using var two = g.Root.CreateChild();
            g.Assert(one, g.Mark, ids[2]);
            g.Assert(two, g.Mark, ids[2]);
            one.FlushUpdates();
            two.FlushUpdates();
            g.Assert(g.Root, g.Mark, ids[2]);
            g.Root.FlushUpdates();
            Assert.True(g.Root.ContainsKey(g.Mark, [ids[2]]));

            var database = g.Root.Context.Database;
            var info = g.EGraph.GetFunctionInfo(g.Edge);
            var rootEdge = database.GetTable(info.Table);
            var rootMark = database.GetTable(g.EGraph.GetFunctionInfo(g.Mark).Table);
            Assert.NotSame(rootEdge.Pending.RowQueue(0), rootMark.Pending.RowQueue(0));
            Assert.NotSame(rootEdge.Pending.RemovalQueue(0), rootMark.Pending.RemovalQueue(0));

            // A child that never staged into edge shares the empty queue with every other lazy layer; the root does not.
            var layer = one.Context.Tables!.GetOrCreateLayer(info.Table).Own;
            var other = two.Context.Tables!.GetOrCreateLayer(info.Table).Own;
            Assert.Same(layer.Pending.RowQueue(0), other.Pending.RowQueue(0));
            Assert.NotSame(layer.Pending.RowQueue(0), rootEdge.Pending.RowQueue(0));

            // The root's notification flags were allocated with its catalog (segment 0 of 128 flags), so its Notify
            // never takes the growth branch that creates a lazy list's first segment.
            var segments = typeof(NotificationList<TableId>).GetField("segments", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
            Assert.True(((int[][])segments.GetValue(database.Catalog.Notifications)!).Length >= 1);
            Assert.True(((int[][])segments.GetValue(new TableCatalog().Notifications)!).Length == 1);
            Assert.Empty((int[][])segments.GetValue(new TableCatalog(lazyNotifications: true).Notifications)!);
        }

        /// <summary>
        /// The root's pending state costs one volatile read for its sealed check: publishing and draining a buffer on a
        /// root table's pending state, with a child open under the root, allocates exactly what it does on a plain
        /// eager state (the buffer's own object; the queues allocate nothing once warm), and a lazy state whose queues
        /// all exist pays the same.
        /// </summary>
        [Fact]
        public void TheRootPublicationPathAllocatesWhatAnEagerStateDoes()
        {
            var g = new ContextGraph();
            var ids = Enumerable.Range(0, 2).Select(_ => g.Root.FreshId()).ToArray();
            using var child = g.Root.CreateChild();
            var table = g.Root.Context.Database.GetTable(g.EGraph.GetFunctionInfo(g.Edge).Table);
            var row = new uint[table.Arity];
            row[0] = ids[0].Rep;
            row[1] = ids[1].Rep;
            row[2] = g.Unit.Rep;

            double PublishAndDrain(PendingState state)
            {
                var buffer = new MutationBuffer(state, table.Arity, 2, true);
                return Measure(() =>
                {
                    buffer.StageInsert(row);
                    buffer.Flush();
                    for (var shard = 0; shard < state.ShardCount; shard++)
                    {
                        while (state.RowQueue(shard).TryDequeue(out var rows))
                            rows.Dispose();
                    }

                    state.TakeTotalRows();
                }, warmup: 2_000, repetitions: 10_000);
            }

            var root = PublishAndDrain(table.Pending);
            var eager = PublishAndDrain(new PendingState(new ShardData(table.Pending.ShardCount)));
            var lazy = new PendingState(new ShardData(table.Pending.ShardCount), lazyQueues: true);
            var opener = new MutationBuffer(lazy, table.Arity, 2, true);
            var cells = new uint[table.Arity];
            for (uint k = 0; k < 64; k++)
            {
                cells[0] = k;
                cells[1] = k;
                cells[2] = g.Unit.Rep;
                opener.StageInsert(cells);
                opener.StageRemove([k, k]);
            }

            opener.Flush();
            lazy.Clear();
            var opened = PublishAndDrain(lazy);
            output.WriteLine($"publish + drain per buffer: root {root:F3} B, eager {eager:F3} B, lazy with every queue {opened:F3} B");
            Assert.True(root <= eager + 0.5, $"a root publication allocated {root:F3} B, a plain eager one {eager:F3} B");
            Assert.True(opened <= eager + 0.5, $"a lazy state with every queue allocated {opened:F3} B per publication, an eager one {eager:F3} B");
        }

        /// <summary>
        /// The frozen root of the copy-volume benchmark: every id has one <c>f</c> row and one <c>r(x, h(x))</c> row
        /// with heavy-tailed hubs.
        /// </summary>
        static (Store Store, uint[] Ids) CopyVolumeRoot(int rows)
        {
            var store = new Store(ParallelHeuristics.Serial);
            var key = new ColumnId(0);
            var f = store.AddTable(new SortedWritesTable(1, 3, new ColumnId(2), [key], new MaxMerge(new ColumnId(1)), ParallelHeuristics.Serial), name: "f");
            var r = store.AddTable(new SortedWritesTable(2, 3, new ColumnId(2), [key, new ColumnId(1)], new MinMerge(new ColumnId(2)), ParallelHeuristics.Serial), name: "r");
            var ids = new uint[rows];
            for (var i = 0; i < rows; i++)
                ids[i] = store.Root.NewId();

            var random = new Random(0xC0F1);
            var permutation = (uint[])ids.Clone();
            random.Shuffle(permutation);
            var hubs = new uint[rows];
            for (var i = 0; i < rows; i++)
            {
                var u = random.NextDouble();
                hubs[i] = permutation[(int)(u * u * u * rows)];
            }

            store.Root.Write(state =>
            {
                for (var i = 0; i < ids.Length; i++)
                {
                    state.StageInsert(f, [ids[i], (uint)(i % 7), state.Timestamp]);
                    state.StageInsert(r, [ids[i], hubs[i], state.Timestamp]);
                }

                return 0;
            });
            store.Root.Freeze();
            return (store, ids);
        }

        /// <summary>
        /// The same unions as fractions of the root's id range: distinct ids at the same relative positions.
        /// </summary>
        static (uint A, uint B) Pick(uint[] ids, double x, double y)
        {
            var a = (int)(x * ids.Length);
            var b = (int)(y * ids.Length);
            if (a == b)
                b = (b + 1) % ids.Length;

            return (ids[a], ids[b]);
        }

        /// <summary>
        /// A child costs in proportion to what it touches: the same unions, each in a child of its own over the
        /// copy-volume root, allocate about the same bytes whatever the root's size (here 20,000 against 200,000 ids,
        /// each pair at the same relative positions in both; at most 1.5 times the bytes over the larger root, with the
        /// rows copied reported).
        /// </summary>
        [Fact]
        public void SingleUnionChildrenDoNotGrowWithTheRoot()
        {
            var small = CopyVolumeRoot(20_000);
            var large = CopyVolumeRoot(200_000);
            var random = new Random(7);
            var pairs = Enumerable.Range(0, 200).Select(_ => (random.NextDouble(), random.NextDouble())).ToArray();

            (double Bytes, long Copied) Run((Store Store, uint[] Ids) root)
            {
                long copied = 0;
                var bytes = Measure(() =>
                {
                    copied = 0;
                    foreach (var (x, y) in pairs)
                    {
                        var (a, b) = Pick(root.Ids, x, y);
                        var child = root.Store.Root.CreateChild();
                        child.Union(a, b);
                        child.Rebuild();
                        copied += child.Tables!.Statistics.RowsCopied;
                        child.Dispose();
                    }
                }, warmup: 2, repetitions: 5);
                return (bytes / pairs.Length, copied);
            }

            var s = Run(small);
            var l = Run(large);
            output.WriteLine($"per single-union child, ids drawn over the whole root: 20k {s.Bytes:N0} B ({s.Copied} rows copied), 200k {l.Bytes:N0} B ({l.Copied} rows copied)");
            Assert.True(l.Bytes <= s.Bytes * 1.5, $"a single-union child allocated {s.Bytes:N0} B over 20k ids and {l.Bytes:N0} B over 200k ids");
        }

        /// <summary>
        /// As <see cref="SingleUnionChildrenDoNotGrowWithTheRoot"/>, with the pairs drawn from the first 2,000 ids of
        /// both roots.
        /// </summary>
        [Fact]
        public void SingleUnionChildrenOverLowIdsDoNotGrowWithTheRoot()
        {
            var small = CopyVolumeRoot(20_000);
            var large = CopyVolumeRoot(200_000);
            var random = new Random(7);
            var pairs = Enumerable.Range(0, 200).Select(_ => (random.Next(2_000), random.Next(2_000))).Where(p => p.Item1 != p.Item2).ToArray();

            (double Bytes, long Copied) Run((Store Store, uint[] Ids) root)
            {
                long copied = 0;
                var bytes = Measure(() =>
                {
                    copied = 0;
                    foreach (var (a, b) in pairs)
                    {
                        var child = root.Store.Root.CreateChild();
                        child.Union(root.Ids[a], root.Ids[b]);
                        child.Rebuild();
                        copied += child.Tables!.Statistics.RowsCopied;
                        child.Dispose();
                    }
                }, warmup: 2, repetitions: 5);
                return (bytes / pairs.Length, copied);
            }

            var s = Run(small);
            var l = Run(large);
            output.WriteLine($"per single-union child: 20k {s.Bytes:N0} B ({s.Copied} rows copied), 200k {l.Bytes:N0} B ({l.Copied} rows copied)");
            Assert.True(l.Bytes <= s.Bytes * 1.5, $"a single-union child allocated {s.Bytes:N0} B over 20k ids and {l.Bytes:N0} B over 200k ids");
        }

        /// <summary>
        /// The copy-volume benchmark's own workload (<c>CopyVolumeBenchmarks.UnionsInOneChild</c>): 200 unions in one
        /// child, a rebuild after each, over roots of 20,000 and 200,000 ids. Per-child bytes do not follow the root.
        /// </summary>
        [Fact]
        public void UnionsInOneChildDoNotGrowWithTheRoot()
        {
            var small = CopyVolumeRoot(20_000);
            var large = CopyVolumeRoot(200_000);
            var random = new Random(11);
            var pairs = Enumerable.Range(0, 200).Select(_ => (random.NextDouble(), random.NextDouble())).ToArray();

            (double Bytes, long Copied) Run((Store Store, uint[] Ids) root)
            {
                long copied = 0;
                var bytes = Measure(() =>
                {
                    var child = root.Store.Root.CreateChild();
                    foreach (var (x, y) in pairs)
                    {
                        var (a, b) = Pick(root.Ids, x, y);
                        child.Union(a, b);
                        child.Rebuild();
                    }

                    copied = child.Tables!.Statistics.RowsCopied;
                    child.Dispose();
                }, warmup: 2, repetitions: 5);
                return (bytes, copied);
            }

            var s = Run(small);
            var l = Run(large);
            output.WriteLine($"200 unions in one child: 20k {s.Bytes:N0} B ({s.Copied} rows copied), 200k {l.Bytes:N0} B ({l.Copied} rows copied)");
            Assert.True(l.Bytes <= s.Bytes * 1.5, $"one child with 200 unions allocated {s.Bytes:N0} B over 20k ids and {l.Bytes:N0} B over 200k ids");
        }

    }

}

using System;
using System.Collections.Generic;
using System.Linq;

using Xunit;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// Review probes for colors M10.4 (root-cost and performance lens): the root keeps its eager pending queues while
    /// cases are open and lifted into it; the root's publication path allocates what an eager state does; and a child's
    /// bytes do not grow with the root (§14.2, the G3 workload).
    /// </summary>
    [Trait("Category", "Performance")]
    [Collection(AllocationProbeCollection.Name)]
    public class LiftRootCostReviewTests
    {

        readonly ITestOutputHelper output;

        public LiftRootCostReviewTests(ITestOutputHelper output)
        {
            this.output = output;
        }

        static (ContextGraph Graph, Value[] Ids) Setup(int count)
        {
            var g = new ContextGraph();
            g.EGraph.DesignateContradiction(g.Bottom);
            var ids = Enumerable.Range(0, count).Select(_ => g.Root.FreshId()).ToArray();
            return (g, ids);
        }

        static LiftSpec EdgeAndMarkSpec(ContextGraph g) => new(
            new LiftTable(g.Edge, LiftTableKind.Relation),
            new LiftTable(g.Mark, LiftTableKind.Relation));

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
        /// §14.1: the root's tables keep their own (eager) pending queues while cases are open, written and lifted into the
        /// root; only the cases' layers share the empty queue until they stage.
        /// </summary>
        [Fact]
        public void TheRootKeepsEagerQueuesWhileCasesAreLifted()
        {
            var (g, ids) = Setup(4);
            g.Assert(g.Root, g.Edge, ids[0], ids[1]);
            g.Root.FlushUpdates();

            using var set = new CaseSet(g.Root);
            var one = set.AddCase();
            var two = set.AddCase();
            g.Assert(one, g.Mark, ids[2]);
            g.Assert(two, g.Mark, ids[2]);
            set.Lift(EdgeAndMarkSpec(g), LiftOptions.Default);
            Assert.True(g.Root.ContainsKey(g.Mark, [ids[2]]));

            var database = g.Root.Context.Database;
            var info = g.EGraph.GetFunctionInfo(g.Edge);
            var rootEdge = database.GetTable(info.Table);
            var rootMark = database.GetTable(g.EGraph.GetFunctionInfo(g.Mark).Table);
            Assert.NotSame(rootEdge.Pending.RowQueue(0), rootMark.Pending.RowQueue(0));
            Assert.NotSame(rootEdge.Pending.RemovalQueue(0), rootMark.Pending.RemovalQueue(0));

            // A case that never staged into edge shares the empty queue with every other lazy layer; the root does not.
            var layer = one.Context.Tables!.GetOrCreateLayer(info.Table).Own;
            var other = two.Context.Tables!.GetOrCreateLayer(info.Table).Own;
            Assert.Same(layer.Pending.RowQueue(0), other.Pending.RowQueue(0));
            Assert.NotSame(layer.Pending.RowQueue(0), rootEdge.Pending.RowQueue(0));

            // The root's notification flags were allocated with its catalog (segment 0 of 128 flags), so its Notify never
            // takes the growth branch that creates a lazy list's first segment.
            var segments = typeof(NotificationList<TableId>).GetField("segments", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
            Assert.True(((int[][])segments.GetValue(database.Catalog.Notifications)!).Length >= 1);
            Assert.True(((int[][])segments.GetValue(new TableCatalog().Notifications)!).Length == 1);
            Assert.Empty((int[][])segments.GetValue(new TableCatalog(lazyNotifications: true).Notifications)!);
        }

        /// <summary>
        /// §14.1 ("Sealed check in PendingState: 1 volatile read"): publishing and draining a buffer on a root table's
        /// pending state, with cases open under the root, allocates exactly what it does on a plain eager state (the
        /// buffer's own object; the queues allocate nothing once warm), and a lazy state whose queues all exist pays the
        /// same.
        /// </summary>
        [Fact]
        public void TheRootPublicationPathAllocatesWhatAnEagerStateDoes()
        {
            var (g, ids) = Setup(2);
            using var set = new CaseSet(g.Root);
            set.AddCase();
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
        /// The frozen root of the copy-volume benchmark (G3): every id has one <c>f</c> row and one <c>r(x, h(x))</c> row
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
        /// §14.2 and the G3 workload: the same unions, each in a child of its own over the copy-volume root, allocate the
        /// same bytes and copy the same rows whatever the root's size (here 20,000 against 200,000 ids, the pairs drawn
        /// from the first 2,000 ids of both).
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
        /// As <see cref="SingleUnionChildrenDoNotGrowWithTheRoot"/>, with the pairs drawn from the first 2,000 ids of both
        /// roots.
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
        /// The G3 benchmark's own workload (<c>CopyVolumeBenchmarks.UnionsInOneChild</c>): 200 unions in one child, a
        /// rebuild after each, over roots of 20,000 and 200,000 ids. Per-child bytes must not follow the root.
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

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Xunit;

using static Eggnog.Relations.Tests.LayeredTestUtil;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// Tests for the storage part of freezing: tables without rebuild columns freeze without a rebuild index, frozen
    /// databases refuse writes, warm-up builds indexes, catalogs refresh serially, compaction at freeze respects the
    /// gate, frozen views fold every hide, and a table added after a freeze is answered through the frozen contexts'
    /// parents, also from concurrent children; and the full freeze: finds on frozen contexts write nothing, from many
    /// threads at once, children working on local ids never write the frozen root's union-find, and no failure nobody
    /// took is frozen into a context.
    /// </summary>
    public class FreezeTests
    {

        readonly ITestOutputHelper output;

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="output">The test output.</param>
        public FreezeTests(ITestOutputHelper output)
        {
            this.output = output;
        }

        /// <summary>
        /// Freezing shares every table and builds a rebuild index only for tables with rebuild columns, at the root and
        /// in a child; frozen databases refuse every write, and a frozen child's views fold every hide.
        /// </summary>
        [Fact]
        public void TablesWithoutRebuildColumnsFreezeWithoutARebuildIndex()
        {
            var store = new Store(ParallelHeuristics.Serial);
            var bottom = store.AddTable(Relation(0), name: "bottom");
            store.DesignateContradiction(bottom);
            var values = store.AddTable(ValueOnly(), name: "values");
            var f = store.AddTable(MaxFunction(1), name: "f");
            var root = store.Root;
            var a = root.NewId();
            Insert(root, values, 1, 10);
            Insert(root, f, a, 1, 0);
            root.Freeze();

            var schema = store.Schema;
            Assert.True(root.Database.IsFrozen);
            Assert.True(schema[bottom]!.Root.IsShared);
            Assert.True(schema[values]!.Root.IsShared);
            Assert.Null(schema[bottom]!.Root.RebuildIndex);
            Assert.Null(schema[values]!.Root.RebuildIndex);
            Assert.NotNull(schema[f]!.Root.RebuildIndex);
            Assert.Equal(schema[f]!.Root.Version, schema[f]!.Root.RebuildIndex!.UpdatedTo);
            Assert.True(schema[f]!.RootInfo.ColumnIndexes.IsSerialRefresh);
            Assert.True(schema[f]!.RootInfo.Indexes.IsSerialRefresh);
            Assert.True(store.RootUnionFind.IsFrozen);
            Assert.True(root.GetView(f).Layers[0]!.IsFrozen);

            // A child writes both and freezes too.
            var child = root.CreateChild();
            Insert(child, values, 1, 20);
            Insert(child, bottom, 0);
            Insert(child, f, a, 2, 0);
            child.Freeze();
            Assert.True(child.Database.IsFrozen);
            Assert.True(child.Tables!.TryGetLayer(values)!.Own.IsShared);
            Assert.Null(child.Tables.TryGetLayer(values)!.Own.RebuildIndex);
            Assert.NotNull(child.Tables.TryGetLayer(f)!.Own.RebuildIndex);
            Assert.True(child.IsContradictory);
            Assert.Equal(20u, Row(child, values, 1)![1]);

            // Frozen databases refuse every write; the frozen views fold every hide.
            Assert.Throws<InvalidOperationException>(() => root.Database.NewBuffer(f));
            Assert.Throws<InvalidOperationException>(() => child.Database.NewBuffer(f));
            Assert.Throws<InvalidOperationException>(() =>
            {
                var buffer = schema[f]!.Root.NewBuffer();
                buffer.StageInsert([a, 9, 9]);
                buffer.Flush();
            });
            var view = child.GetView(f);
            Assert.Null(view.Layers[0]!.Hidden.Live);
            Assert.NotNull(view.Layers[0]!.Hidden.Frozen);
            Assert.True(view.Layers[1]!.IsFrozen);
            LookupPaths.Check(child);
        }

        /// <summary>
        /// A freeze with staged, unmerged writes throws and leaves the context, its database, its union-find and its
        /// tables writable.
        /// </summary>
        [Fact]
        public void AFreezeWithUnmergedWritesChangesNothing()
        {
            var store = new Store(ParallelHeuristics.Serial);
            var f = store.AddTable(MaxFunction(1), name: "f");
            var root = store.Root;
            var buffer = store.Schema[f]!.Root.NewBuffer();
            buffer.StageInsert([root.NewId(), 1, 1]);
            buffer.Flush();

            Assert.Throws<InvalidOperationException>(() => root.Freeze());
            Assert.False(root.IsFrozen);
            Assert.False(root.Database.IsFrozen);
            Assert.False(store.RootUnionFind.IsFrozen);
            Assert.False(store.Schema[f]!.Root.IsShared);
        }

        /// <summary>
        /// A freeze builds the indexes its warm-up names, a column index for one column and a tuple index for more,
        /// skipping keys of tables the context does not have; a key without columns is refused.
        /// </summary>
        [Fact]
        public void WarmupBuildsTheIndexesAFrozenContextShares()
        {
            var store = new Store(ParallelHeuristics.Serial);
            var f = store.AddTable(MaxFunction(2), name: "f");
            var root = store.Root;
            Insert(root, f, root.NewId(), root.NewId(), 1, 0);
            root.Freeze(new FreezeWarmup([new CachedIndexKey(f, [C(0)]), new CachedIndexKey(f, [C(0), C(1)]), new CachedIndexKey(new TableId(9), [C(0)])]));

            var info = store.Schema[f]!.RootInfo;
            Assert.Equal(1, info.ColumnIndexes.Count);
            Assert.Equal(1, info.Indexes.Count);
            Assert.Throws<ArgumentException>(() => new CachedIndexKey(f, []).CheckedColumns.ToArray());
        }

        /// <summary>
        /// A freeze does not compact a table while a live child's hides index its rows, and the child reads through the
        /// stale slots; without descendants, a freeze compacts.
        /// </summary>
        [Fact]
        public void CompactionAtFreezeRespectsTheGate()
        {
            var store = new Store(ParallelHeuristics.Serial);
            var f = store.AddTable(MaxFunction(1), name: "f");
            var root = store.Root;
            var ids = Enumerable.Range(0, 64).Select(_ => root.NewId()).ToArray();
            root.Write(state =>
            {
                foreach (var id in ids)
                    state.StageInsert(f, [id, 1, state.Timestamp]);

                return 0;
            });

            // Half the rows replaced: tombstones below the automatic threshold (more than half), so only a forced
            // compaction removes them.
            root.Write(state =>
            {
                foreach (var id in ids.Take(30))
                    state.StageInsert(f, [id, 2, state.Timestamp]);

                return 0;
            });

            var table = store.Schema[f]!.Root;
            Assert.Equal(30, table.StaleCount);
            Assert.Same(root.Gate, table.CompactionGate);

            // A live child blocks it: its hides index the root's rows.
            var child = root.CreateChild();
            Insert(child, f, ids[40], 5, 0);
            root.Freeze();
            Assert.Equal(30, table.StaleCount);
            Assert.True(table.CompactionsBlocked > 0);
            Assert.False(child.Tables!.NeedsCatchUp);
            Assert.Equal(5u, Row(child, f, ids[40])![1]);
            Assert.Equal(2u, Row(child, f, ids[0])![1]);
            LookupPaths.Check(child);

            // Without descendants a freeze compacts.
            var other = new Store(ParallelHeuristics.Serial);
            var g = other.AddTable(MaxFunction(1), name: "g");
            var otherIds = Enumerable.Range(0, 64).Select(_ => other.Root.NewId()).ToArray();
            foreach (var value in new uint[] { 1, 2 })
            {
                other.Root.Write(state =>
                {
                    foreach (var id in otherIds.Take(value == 1 ? 64 : 30))
                        state.StageInsert(g, [id, value, state.Timestamp]);

                    return 0;
                });
            }

            Assert.Equal(30, other.Schema[g]!.Root.StaleCount);
            other.Root.Freeze();
            Assert.Equal(0, other.Schema[g]!.Root.StaleCount);
            Assert.Equal(64, other.Schema[g]!.Root.Count);
        }

        /// <summary>
        /// A table added after the root and a child froze is frozen and empty at the root; the frozen child answers for
        /// it through its parent without a layer, and children of the frozen child write and read it concurrently.
        /// </summary>
        [Fact]
        public void ATableAddedAfterAFreezeIsAnsweredThroughTheParents()
        {
            var store = new Store(ParallelHeuristics.Serial);
            var f = store.AddTable(MaxFunction(1), name: "f");
            var root = store.Root;
            var ids = Enumerable.Range(0, 64).Select(_ => root.NewId()).ToArray();
            Insert(root, f, ids[0], 1, 0);
            root.Freeze();
            var middle = root.CreateChild();
            Insert(middle, f, ids[1], 1, 0);
            middle.Freeze();

            // The new table is frozen at the root, empty, with a frozen root view; the frozen middle answers for it
            // through its parent and never writes its arrays.
            var g = store.AddTable(MaxFunction(1), name: "g");
            var info = store.Schema[g]!;
            Assert.True(info.Root.IsShared);
            Assert.True(info.RootInfo.ColumnIndexes.IsSerialRefresh);
            Assert.Same(store.Schema.RootView(g), root.GetView(g));
            Assert.True(root.GetView(g).Layers[0]!.IsFrozen);
            Assert.Same(store.Schema.RootView(g), middle.GetView(g));
            Assert.Null(middle.Tables!.TryGetLayer(g));
            Assert.Throws<InvalidOperationException>(() => middle.Tables.GetOrCreateLayer(g));

            // Children of the frozen middle, on threads of their own, write and read the new table.
            const int Children = 8;
            var children = Enumerable.Range(0, Children).Select(_ => middle.CreateChild()).ToArray();
            var failures = new List<Exception>();
            using var start = new Barrier(Children);
            var tasks = children.Select((child, index) => Task.Run(() =>
            {
                try
                {
                    start.SignalAndWait();
                    for (var i = 0; i < 20; i++)
                    {
                        Insert(child, g, ids[(index * 7 + i) % ids.Length], (uint)(index + i), 0);
                        Insert(child, f, ids[i % 2], (uint)(index + 2), 0);
                        Assert.NotNull(Row(child, g, ids[(index * 7 + i) % ids.Length]));
                        LookupPaths.Check(child);
                    }
                }
                catch (Exception e)
                {
                    lock (failures)
                        failures.Add(e);
                }
            })).ToArray();

            Task.WaitAll(tasks);
            Assert.Empty(failures);
            foreach (var child in children)
            {
                Assert.Equal(20, child.GetView(g).VisibleCount);
                Assert.Equal(2, child.GetView(f).VisibleCount);
                child.Dispose();
            }

            Assert.Null(middle.Tables.TryGetLayer(g));
            Assert.Equal(0, store.Schema[g]!.Root.Count);
            Assert.Same(store.Schema.RootView(g), middle.GetView(g));
        }

        /// <summary>
        /// Frozen finds do not write: 8 threads find 10⁵ ids each on a frozen chain (a frozen root with unions, a
        /// frozen child with its own unions over it, a frozen grandchild with its flattened chain), plus as many ids
        /// beyond the root's array, while siblings of the frozen grandchild mint, union and rebuild, and find their own
        /// local ids; the root's parent array (its length included), the overlays and the journals are bit-identical
        /// afterwards, and every find agrees with a serial reading taken before.
        /// </summary>
        [Fact]
        public void FrozenFindsDoNotWrite()
        {
            const int Ids = 20_000;
            const int FindsPerThread = 100_000;
            var store = new Store(ParallelHeuristics.Serial);
            var f = store.AddTable(MaxFunction(1), name: "f");
            var root = store.Root;
            for (var i = 0; i < Ids; i++)
                root.NewId();

            var random = new Random(3);
            root.Write((random, store.UnionFindTable), static (state, args) =>
            {
                Span<uint> row = stackalloc uint[3];
                for (var i = 0; i < Ids / 2; i++)
                {
                    row[0] = (uint)args.random.Next(Ids);
                    row[1] = (uint)args.random.Next(Ids);
                    row[2] = state.Timestamp;
                    state.StageInsert(args.UnionFindTable, row);
                }

                return 0;
            });
            root.Write(state =>
            {
                for (var i = 0; i < 1000; i++)
                    state.StageInsert(f, [(uint)i, 1, state.Timestamp]);

                return 0;
            });
            root.Rebuild();
            root.Freeze();

            var child = root.CreateChild();
            for (var i = 0; i < 500; i++)
                child.Union((uint)random.Next(Ids), (uint)random.Next(Ids));

            child.Rebuild();
            child.Freeze();
            var grandchild = child.CreateChild();
            for (var i = 0; i < 100; i++)
                grandchild.Union((uint)random.Next(Ids), (uint)random.Next(Ids));

            grandchild.Rebuild();
            grandchild.Freeze();

            // A frozen root is fully compressed, so its parent array is its find.
            var parents = store.RootUnionFind.UnionFind.UnionFind.Parents.ToArray();
            var overlays = new[] { child, grandchild }.Select(c => (c.Overlay!.Version, c.Overlay.MemberCount, c.Journal!.Ids.ToArray())).ToArray();
            var expected = Enumerable.Range(0, Ids).Select(i => grandchild.Find((uint)i)).ToArray();
            var expectedChild = Enumerable.Range(0, Ids).Select(i => child.Find((uint)i)).ToArray();

            var failures = 0;
            var threads = Enumerable.Range(0, 8).Select(t => new Thread(() =>
            {
                var local = new Random(100 + t);
                for (var k = 0; k < FindsPerThread; k++)
                {
                    var x = (uint)local.Next(Ids);
                    if (grandchild.Find(x) != expected[x] || child.Find(x) != expectedChild[x] || root.Find(x) != parents[x])
                        Interlocked.Increment(ref failures);

                    // Ids beyond the root's array (never minted there, or minted in a sibling's blocks) are their own
                    // canonical along the frozen chain, and finding them grows no frozen array.
                    var beyond = (uint)(Ids + local.Next(4096));
                    if (grandchild.Find(beyond) != beyond || child.Find(beyond) != beyond || root.Find(beyond) != beyond)
                        Interlocked.Increment(ref failures);

                    // A sibling under the frozen child mints, writes and rebuilds on its own domain meanwhile, and
                    // finds its local ids through the frozen chain.
                    if (t == 0 && k % 20_000 == 0)
                    {
                        var sibling = child.CreateChild();
                        var fresh = sibling.NewId();
                        sibling.Union((uint)local.Next(Ids), (uint)local.Next(Ids));
                        sibling.Union(fresh, sibling.NewId());
                        sibling.Rebuild();
                        if (sibling.Find(fresh) != fresh)
                            Interlocked.Increment(ref failures);

                        sibling.Dispose();
                    }
                }
            })).ToArray();

            foreach (var thread in threads)
                thread.Start();

            foreach (var thread in threads)
                thread.Join();

            Assert.Equal(0, failures);
            Assert.Equal(parents, store.RootUnionFind.UnionFind.UnionFind.Parents.ToArray());
            var after = new[] { child, grandchild }.Select(c => (c.Overlay!.Version, c.Overlay.MemberCount, c.Journal!.Ids.ToArray())).ToArray();
            for (var i = 0; i < overlays.Length; i++)
            {
                Assert.Equal(overlays[i].Version, after[i].Version);
                Assert.Equal(overlays[i].MemberCount, after[i].MemberCount);
                Assert.Equal(overlays[i].Item3, after[i].Item3);
            }

            grandchild.CheckInvariants();
            child.CheckInvariants();
            root.CheckInvariants();
        }

        /// <summary>
        /// Freezing refuses a writable parent, a stopped or faulted context and a failure nobody took, and changes
        /// nothing; a second freeze is a no-op; freezing a context with live children is allowed, and the children
        /// catch up once.
        /// </summary>
        [Fact]
        public void FreezeRefusesWhatItCannotShare()
        {
            var store = new Store(ParallelHeuristics.Serial);
            var f = store.AddTable(MaxFunction(1), name: "f");
            var root = store.Root;
            var a = root.NewId();
            var child = root.CreateChild();
            Assert.Throws<InvalidOperationException>(() => child.Freeze());

            root.Write(state =>
            {
                state.ReportFailure("boom");
                return 0;
            });
            Assert.Throws<InvalidOperationException>(() => root.Freeze());
            Assert.False(root.IsFrozen);
            Assert.Equal("boom", root.Database.TakeFailure());

            Insert(root, f, a, 2, 0);
            root.Freeze();
            root.Freeze();
            Assert.True(root.IsFrozen);
            child.Rebuild();
            Assert.Equal(2u, Row(child, f, a)![1]);

            var faulted = root.CreateChild();
            Assert.Throws<InvalidOperationException>(() => faulted.Write<int>(_ => throw new InvalidOperationException("inside")));
            Assert.True(faulted.IsFaulted);
            Assert.Throws<InvalidOperationException>(() => faulted.Freeze());
            Assert.False(faulted.IsFrozen);
            faulted.Dispose();
        }

        /// <summary>
        /// A failure the freeze itself causes: the root's settle (a stale raw write folding into its canonical key
        /// under an asserting merge) and a child's rebuild (which copies g(b)=2 onto g(a)=1) record a failure; the
        /// freeze refuses after them, without faulting, and leaves the context unfrozen and clean; once the failure is
        /// taken, the freeze succeeds.
        /// </summary>
        [Fact]
        public void AFailureTheFreezeItselfRecordsIsRefusedWithoutFaulting()
        {
            var store = new Store(ParallelHeuristics.Serial);
            var g = store.AddTable(new SortedWritesTable(1, 3, C(2), [C(0)], new AssertEqualMerge(C(1), "g"), ParallelHeuristics.Serial), name: "g");
            var root = store.Root;
            var a = root.NewId();
            var b = root.NewId();
            var c = root.NewId();
            var d = root.NewId();

            // The root's settle: d lost to c, then g(c)=1 and the stale g(d)=2 are written raw.
            root.Union(c, d);
            root.Rebuild();
            Insert(root, g, c, 1, 0);
            Insert(root, g, d, 2, 0);
            Insert(root, g, a, 1, 0);
            Insert(root, g, b, 2, 0);
            Assert.Null(root.Database.Failure);
            Assert.Throws<InvalidOperationException>(() => root.Freeze());
            Assert.False(root.IsFrozen);
            Assert.NotNull(root.Database.TakeFailure());
            root.Freeze();
            Assert.True(root.IsFrozen);

            // The child's rebuild.
            var child = root.CreateChild();
            child.Union(a, b);
            Assert.Null(child.Database.Failure);
            Assert.Throws<InvalidOperationException>(() => child.Freeze());
            Assert.False(child.IsFrozen);
            Assert.False(child.IsFaulted);
            Assert.True(child.IsClean);
            Assert.NotNull(child.Database.TakeFailure());
            child.Freeze();
            Assert.True(child.IsFrozen);
            Assert.Equal(1u, Row(child, g, a)![1]);
        }

        /// <summary>
        /// Children of a frozen root find, union and rebuild over ids of their own, which lie above every id the root
        /// reserved, from many threads at once. The root's parent array stays the same object with the same contents: a
        /// find that reserved or compressed on the way (egglog's writing <c>find</c> in place of <c>FindNaive</c>)
        /// would grow or rewrite it from many threads at once.
        /// </summary>
        [Fact]
        public void ChildrenWorkingOnLocalIdsNeverWriteTheFrozenRootsUnionFind()
        {
            const int Ids = 200;
            var store = new Store(ParallelHeuristics.Serial);
            var f = store.AddTable(MaxFunction(1), name: "f");
            var root = store.Root;
            var ids = Enumerable.Range(0, Ids).Select(_ => root.NewId()).ToArray();
            for (var i = 0; i + 1 < Ids; i += 3)
                root.Union(ids[i], ids[i + 1]);

            for (var i = 0; i < Ids; i += 2)
                Insert(root, f, ids[i], (uint)i, 0);

            root.Rebuild();
            root.Freeze();

            var before = store.RootUnionFind.UnionFind.UnionFind.Parents.ToArray();
            var failures = 0;
            var threads = Enumerable.Range(0, 8).Select(t => new Thread(() =>
            {
                try
                {
                    var random = new Random(7 + t);
                    for (var round = 0; round < 20; round++)
                    {
                        using var child = root.CreateChild();
                        var local = Enumerable.Range(0, 6).Select(_ => child.NewId()).ToArray();
                        child.Union(local[0], local[1]);
                        child.Union(local[2], ids[random.Next(Ids)]);
                        child.Union(ids[random.Next(Ids)], ids[random.Next(Ids)]);
                        Insert(child, f, local[3], 1, 0);
                        Insert(child, f, local[4], 2, 0);
                        child.Rebuild();
                        foreach (var id in local)
                            child.Find(id);

                        child.CheckInvariants();
                    }
                }
                catch (Exception)
                {
                    Interlocked.Increment(ref failures);
                }
            })).ToArray();

            foreach (var thread in threads)
                thread.Start();

            foreach (var thread in threads)
                thread.Join();

            Assert.Equal(0, failures);
            Assert.Equal(before, store.RootUnionFind.UnionFind.UnionFind.Parents.ToArray());
        }

        /// <summary>
        /// A failure recorded and not taken is never frozen into a context that descendants share, also when the
        /// freeze's own rebuild records it (a merge conflict the rebuild finds when it copies a row onto its winner's
        /// key).
        /// </summary>
        [Fact]
        public void FreezeDoesNotFreezeAFailureItsOwnRebuildRecorded()
        {
            var store = new Store(ParallelHeuristics.Serial);
            var g = store.AddTable(new SortedWritesTable(1, 3, C(2), [C(0)], new AssertEqualMerge(C(1), "g"), ParallelHeuristics.Serial), name: "g");
            var root = store.Root;
            var a = root.NewId();
            var b = root.NewId();
            Insert(root, g, a, 1, 0);
            Insert(root, g, b, 2, 0);
            root.Freeze();

            var child = root.CreateChild();
            child.Union(a, b);
            Assert.Null(child.Database.Failure);

            Exception? thrown = null;
            try
            {
                child.Freeze();
            }
            catch (InvalidOperationException e)
            {
                thrown = e;
            }

            output.WriteLine($"frozen: {child.IsFrozen}, failure: {child.Database.Failure ?? "<none>"}, threw: {thrown?.Message}");
            Assert.False(child.IsFrozen && child.Database.Failure is not null, $"the child froze with the failure '{child.Database.Failure}' that its freeze's rebuild recorded");
        }

    }

}

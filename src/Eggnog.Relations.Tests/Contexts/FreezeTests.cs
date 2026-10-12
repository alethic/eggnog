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
    /// Tests for the storage part of freezing (colored-contexts spec §8.1, §4.3, X13, X14, X43; review finding R2-5):
    /// tables without rebuild columns freeze without a rebuild index, frozen databases refuse writes, warm-up builds
    /// indexes, catalogs refresh serially, compaction at freeze respects the gate, frozen views fold every hide, and a
    /// table added after a freeze is answered through the frozen contexts' parents, also from concurrent children.
    /// </summary>
    public class FreezeTests
    {

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

            // X43: the new table is frozen at the root, empty, with a frozen root view; the frozen middle answers for it
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

    }

}

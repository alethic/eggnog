using System;
using System.Collections.Generic;
using System.Linq;

using Xunit;

using static Eggnog.Relations.Tests.LayeredTestUtil;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// Tests for the layered merge of a child: new keys, keys the child sees through an ancestor with merges that
    /// change and that do not, folds, a constructor's union staged into the child, the lattice check (only the root may
    /// write a table whose merge is not a lattice merge), deletes, and reads of the stratum being merged.
    /// </summary>
    public class LayeredMergeTests
    {

        static (Store Store, TableId F, Context Root, uint[] Ids) Setup(int ids = 4)
        {
            var store = new Store(ParallelHeuristics.Serial);
            var f = store.AddTable(MaxFunction(1), name: "f");
            var root = store.Root;
            return (store, f, root, Enumerable.Range(0, ids).Select(_ => root.NewId()).ToArray());
        }

        [Fact]
        public void ANewKeyIsAppendedToTheOwnLayer()
        {
            var (_, f, root, ids) = Setup();
            Insert(root, f, ids[0], 1, 0);
            root.Freeze();
            var child = root.CreateChild();
            var version = child.Version;
            Insert(child, f, ids[1], 3, 0);

            var layer = child.Tables!.TryGetLayer(f)!;
            Assert.Equal(1, layer.Own.Count);
            Assert.Null(layer.HiddenOver(0));
            Assert.Equal(0, child.Tables.Statistics.RowsHidden);
            Assert.True(child.Version > version);
            Assert.Equal(3u, Row(child, f, ids[1])![1]);
            Assert.Equal(1u, Row(child, f, ids[0])![1]);
            Assert.Null(Row(root, f, ids[1]));
            LookupPaths.Check(child);
        }

        [Fact]
        public void AnAncestorKeyMergesWithTheVisibleRow()
        {
            var (store, f, root, ids) = Setup();
            Insert(root, f, ids[0], 4, 0);
            root.Freeze();
            var child = root.CreateChild();

            // The merge keeps the visible row: nothing is stored, nothing hidden, the version does not move.
            var version = child.Version;
            Insert(child, f, ids[0], 2, 0);
            var layer = child.Tables!.TryGetLayer(f)!;
            Assert.Equal(0, layer.Own.Count);
            Assert.Null(layer.HiddenOver(0));
            Assert.Equal(version, child.Version);
            Assert.Equal(4u, Row(child, f, ids[0])![1]);

            // The merge changes it: the output is stored and the ancestor row hidden after it exists.
            Insert(child, f, ids[0], 9, 0);
            Assert.Equal(1, layer.Own.Count);
            var rootRow = store.Schema[f]!.Root.FindRow(ids[0]);
            Assert.True(layer.IsHiddenHere(0, rootRow));
            Assert.Equal(1, child.Tables.Statistics.RowsHidden);
            Assert.Equal(9u, Row(child, f, ids[0])![1]);
            Assert.Equal(4u, Row(root, f, ids[0])![1]);
            LookupPaths.Check(child);
        }

        [Fact]
        public void RowsOfOneKeyFoldLeftFromTheVisibleRow()
        {
            var (_, f, root, ids) = Setup();
            Insert(root, f, ids[0], 1, 0);
            root.Freeze();
            var child = root.CreateChild();
            child.Write(state =>
            {
                state.StageInsert(f, [ids[0], 3, state.Timestamp]);
                state.StageInsert(f, [ids[0], 2, state.Timestamp]);
                state.StageInsert(f, [ids[0], 5, state.Timestamp]);
                return 0;
            });

            // 3 merges with the root's 1 (stored, root row hidden), 2 merges with the own 3 (nothing), 5 replaces 3.
            var own = child.Tables!.TryGetLayer(f)!.Own;
            Assert.Equal(2, own.Rows.Count);
            Assert.Equal(1, own.StaleCount);
            Assert.Equal(5u, Row(child, f, ids[0])![1]);
            Assert.Equal(1, child.Tables.Statistics.RowsHidden);
            LookupPaths.Check(child);
        }

        [Fact]
        public void AConstructorStagesItsUnionIntoTheChild()
        {
            var store = new Store(ParallelHeuristics.Serial);
            var g = store.AddTable(Constructor(store), [], [store.UnionFindTable], "g");
            var root = store.Root;
            var a = root.NewId();
            var low = root.NewId();
            var high = root.NewId();
            Insert(root, g, a, high, 0);
            root.Freeze();
            var child = root.CreateChild();

            // g(a) = low against the visible g(a) = high: the union goes to the child's union-find, the smaller id is kept.
            Insert(child, g, a, low, 0);
            Assert.Equal(1, child.UnionFindStatistics.OwnUnions);
            Assert.Equal(1, child.DisplacedCount);
            Assert.Equal(0, store.RootUnionFind.UnionFind.Count);
            child.Rebuild();
            Assert.True(child.AreEqual(low, high));
            Assert.False(root.AreEqual(low, high));
            Assert.Equal(low, Row(child, g, a)![1]);
            Assert.Equal(high, Row(root, g, a)![1]);
            LookupPaths.Check(child);

            // The other way round, the visible row is kept and nothing is stored, but the union is still made.
            var other = root.CreateChild();
            var fresh = other.NewId();
            Insert(other, g, a, fresh, 0);
            other.Rebuild();
            Assert.True(other.AreEqual(fresh, high));
            Assert.Equal(0, other.Tables!.TryGetLayer(g)!.Own.Count);
            Assert.Equal(high, Row(other, g, a)![1]);
        }

        [Fact]
        public void ANonLatticeTableThrowsAtTheFirstWriteNotAtAHittingLookup()
        {
            var store = new Store(ParallelHeuristics.Serial);
            var h = store.AddTable(new SortedWritesTable(1, 3, C(2), [C(0)], new KeepCurrentMerge(), ParallelHeuristics.Serial), name: "h");
            var p = store.AddTable(new SortedWritesTable(1, 3, C(2), [C(0)], new KeepCurrentMerge(), ParallelHeuristics.Serial), name: "p", contextPrivate: true);
            var root = store.Root;
            var a = root.NewId();
            var b = root.NewId();
            Insert(root, h, a, 7, 0);
            root.Freeze();
            var child = root.CreateChild();

            // Reads that hit create no layer and do not throw.
            var state = child.Database.CreateExecutionState(child.Store.ReadClock());
            Assert.Equal(7u, state.PredictColumn(h, [a], [MergeVal.FromConstant(new Value(1)), MergeVal.FromConstant(new Value(0))], C(1)));
            Assert.Null(child.Tables!.TryGetLayer(h));

            // The first write (here a prediction that misses and stages) is refused: the merge is not a lattice merge.
            var error = Assert.Throws<InvalidOperationException>(() => state.PredictColumn(h, [b], [MergeVal.FromConstant(new Value(1)), MergeVal.FromConstant(new Value(0))], C(1)));
            Assert.Contains("only the root", error.Message);
            Assert.Throws<InvalidOperationException>(() => state.StageInsert(h, [b, 1, state.Timestamp]));
            Assert.Null(child.Tables.TryGetLayer(h));

            // Through a write batch the refusal faults the child, which then allows only Dispose.
            Assert.Throws<InvalidOperationException>(() => Insert(child, h, b, 1, 0));
            Assert.True(child.IsFaulted);
            child.Dispose();

            // A context-private table is the child's alone, so any merge may be written.
            var other = root.CreateChild();
            Insert(other, p, a, 3, 0);
            Assert.Equal(3u, Row(other, p, a)![1]);
        }

        [Fact]
        public void DeletesTombstoneOwnRowsAndHideAncestorRows()
        {
            var (store, f, root, ids) = Setup();
            Insert(root, f, ids[0], 1, 0);
            Insert(root, f, ids[1], 1, 0);
            root.Union(ids[3], ids[2]);
            root.Rebuild();
            root.Freeze();
            var child = root.CreateChild();
            Insert(child, f, ids[2], 5, 0);
            var version = child.Version;

            // An own row is tombstoned; an ancestor row is hidden (Retracted); an absent key changes nothing.
            child.Write(state =>
            {
                state.StageRemove(f, [ids[2]]);
                state.StageRemove(f, [ids[0]]);
                return 0;
            });

            var layer = child.Tables!.TryGetLayer(f)!;
            Assert.Equal(1, layer.Own.StaleCount);
            Assert.True(layer.HasRetractions);
            Assert.True(layer.IsHiddenHere(0, store.Schema[f]!.Root.FindRow(ids[0])));
            Assert.Null(Row(child, f, ids[0]));
            Assert.Null(Row(child, f, ids[2]));
            Assert.Equal(1u, Row(child, f, ids[1])![1]);
            Assert.True(child.Version > version);
            Assert.Equal(1u, Row(root, f, ids[0])![1]);

            // A delete with a stale key (ids[3] lost to ids[2] at the root) matches nothing: the visible rows are
            // canonical.
            Insert(child, f, ids[2], 6, 0);
            version = child.Version;
            Remove(child, f, ids[3]);
            Assert.Equal(6u, Row(child, f, ids[2])![1]);
            Assert.Equal(version, child.Version);
            LookupPaths.Check(child);
        }

        [Fact]
        public void AMergeFunctionReadingItsOwnStratumThrows()
        {
            // t and u share a stratum; u's merge reads t while both merge. The root throws KeyNotFoundException (t is
            // taken out), a child InvalidOperationException.
            var store = new Store(ParallelHeuristics.Serial);
            var t = store.AddTable(MaxFunction(1), name: "t");
            var u = store.AddTable(new SortedWritesTable(1, 3, C(2), [C(0)], new StratumReadingMerge(t), ParallelHeuristics.Serial), name: "u");
            var root = store.Root;
            var a = root.NewId();
            var b = root.NewId();
            Insert(root, u, a, 1, 0);

            static void Both(Context context, TableId t, TableId u, uint a, uint b) => context.Write(state =>
            {
                state.StageInsert(t, [b, 1, state.Timestamp]);
                state.StageInsert(u, [a, 2, state.Timestamp]);
                return 0;
            });

            var probe = root.CreateChild();
            Assert.Throws<InvalidOperationException>(() => Both(probe, t, u, a, b));
            Assert.True(probe.IsFaulted);
            probe.Dispose();

            Assert.Throws<KeyNotFoundException>(() => Both(root, t, u, a, b));
            Assert.False(root.IsFaulted);

            // Read through a lower stratum it works: a child whose merge of u reads t when t is not merging.
            root.Rebuild();
            root.Freeze();
            var child = root.CreateChild();
            Insert(child, t, b, 3, 0);
            Insert(child, u, a, 5, 0);
            Assert.Equal(5u, Row(child, u, a)![1]);
        }

        [Fact]
        public void AWriteStampsEveryIncomingRowWithTheMergesEpoch()
        {
            var (_, f, root, ids) = Setup();
            root.Freeze();
            var child = root.CreateChild();
            var epoch = 0u;
            child.Write(state =>
            {
                epoch = state.Timestamp;
                state.StageInsert(f, [ids[0], 1, 12345]);
                return 0;
            });

            Assert.Equal(epoch, Row(child, f, ids[0])![2]);
            Assert.True(epoch >= child.CreatedAt);
        }

    }

}

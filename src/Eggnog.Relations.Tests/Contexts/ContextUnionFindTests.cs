using System;
using System.Linq;

using Xunit;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// Tests for a context's union-find API (colored-contexts spec §5.6, §5.7, §17 M9.2 interim): unions, finds and the
    /// cleanliness they need, catch-up after a parent change with its statistics, own displacements, the minimal write,
    /// faulting, the union-find-only freeze and the finders it hands to children.
    /// </summary>
    public class ContextUnionFindTests
    {

        [Fact]
        public void RootUnionsNeedARebuildBeforeTheyAreRead()
        {
            var store = new Store();
            var root = store.Root;
            var a = root.NewId();
            var b = root.NewId();
            Assert.True(root.IsClean);
            Assert.Same(store.RootUnionFind, root.UnionFindTable);

            Assert.True(root.Union(b, a));
            Assert.False(root.Union(a, b));
            Assert.False(root.IsClean);
            Assert.Throws<InvalidOperationException>(() => root.Find(b));
            Assert.Equal(1, root.DisplacedCount);
            Assert.Equal(new[] { b }, root.DisplacedIds.ToArray());

            var clock = store.ReadClock();
            Assert.False(root.Rebuild());
            Assert.True(root.IsClean);
            Assert.Equal(a, root.Find(b));
            Assert.True(root.AreEqual(a, b));
            Assert.Equal(clock + 1, store.ReadClock());

            // A clean root rebuilds nothing and leaves the clock alone (X42).
            Assert.False(root.Rebuild());
            Assert.Equal(clock + 1, store.ReadClock());
            Assert.Equal(new UnionFindStatistics(1, 0, 0, 0), root.UnionFindStatistics);
            Assert.False(root.IsOwnDisplacement(b));
        }

        [Fact]
        public void ARootRebuildRewritesItsRows()
        {
            var store = new Store();
            var root = store.Root;
            var f = store.AddTable(StoreTests.Function(store), [], [store.UnionFindTable], "f");
            var a = root.NewId();
            var b = root.NewId();
            var r = root.NewId();
            root.Write(s =>
            {
                s.StageInsert(f, [b, r, s.Timestamp]);
                return 0;
            });
            root.Union(a, b);
            Assert.True(root.Rebuild());
            var rows = TableTestUtil.LiveRows(root.Database.GetTable(f));
            Assert.Equal(new[] { a }, rows.Select(row => row[0]).ToArray());
        }

        [Fact]
        public void AChildUnionsOverItsParentsCanonicalIds()
        {
            var store = new Store();
            var root = store.Root;
            var ids = Enumerable.Range(0, 6).Select(_ => root.NewId()).ToArray();
            root.Union(ids[1], ids[0]);
            root.Rebuild();

            var child = root.CreateChild();
            Assert.IsType<OverlayDisplacedTable>(child.UnionFindTable);
            Assert.True(child.IsClean);
            Assert.True(child.IsUnionFindCurrent);

            // ids[1] is ids[0] in the parent: the child merges ids[0]'s class with ids[3]; ids[3] loses.
            Assert.True(child.Union(ids[3], ids[1]));
            Assert.False(child.IsClean);
            child.Rebuild();
            Assert.Equal(ids[0], child.Find(ids[3]));
            Assert.Equal(ids[0], child.Find(ids[1]));
            Assert.Equal(new[] { ids[3] }, child.DisplacedIds.ToArray());
            Assert.True(child.IsOwnDisplacement(ids[3]));

            // The parent is unchanged.
            Assert.Equal(ids[3], root.Find(ids[3]));
            Assert.Equal(1, root.DisplacedCount);
            Assert.Equal(new UnionFindStatistics(1, 0, 0, 0), child.UnionFindStatistics);
            child.CheckInvariants();
        }

        [Fact]
        public void AChildCatchesUpWithItsParentAtItsNextRebuild()
        {
            var store = new Store();
            var root = store.Root;
            var ids = Enumerable.Range(0, 8).Select(_ => root.NewId()).ToArray();
            var child = root.CreateChild();
            child.Union(ids[2], ids[5]);
            child.Rebuild();

            // The parent displaces 5 (a member of the child's class) and 7 (an id the child never touched).
            root.Union(ids[5], ids[4]);
            root.Union(ids[7], ids[6]);
            Assert.False(child.IsClean);
            Assert.False(child.IsUnionFindCurrent);
            Assert.Throws<InvalidOperationException>(() => child.Find(ids[5]));

            Assert.True(child.Rebuild());
            Assert.True(child.IsUnionFindCurrent);
            Assert.Equal(ids[2], child.Find(ids[4]));
            Assert.Equal(ids[2], child.Find(ids[5]));
            Assert.Equal(ids[6], child.Find(ids[7]));

            // Journal: the own loser 5, then the rekeyed canonical 4 (now under 2), and the import 7.
            Assert.Equal(new[] { ids[5], ids[4], ids[7] }, child.DisplacedIds.ToArray());
            Assert.True(child.IsOwnDisplacement(ids[4]));
            Assert.False(child.IsOwnDisplacement(ids[5]));
            Assert.False(child.IsOwnDisplacement(ids[7]));
            var stats = child.UnionFindStatistics;
            Assert.Equal((1L, 1L, 1L), (stats.OwnUnions, stats.ImportedDisplacements, stats.RekeyedMembers));
            child.CheckInvariants();
        }

        [Fact]
        public void WritesCatchUpFirst()
        {
            var store = new Store();
            var root = store.Root;
            var ids = Enumerable.Range(0, 4).Select(_ => root.NewId()).ToArray();
            var child = root.CreateChild();
            root.Union(ids[1], ids[0]);

            // The parent was not rebuilt and the child is behind: the write rebuilds both before it stages.
            Assert.True(child.Union(ids[1], ids[2]));
            Assert.True(root.IsClean);
            Assert.True(child.IsUnionFindCurrent);
            child.Rebuild();
            Assert.Equal(ids[0], child.Find(ids[2]));
            Assert.Equal(new[] { ids[1], ids[2] }, child.DisplacedIds.ToArray());
        }

        [Fact]
        public void CreateChildRebuildsADirtyParent()
        {
            var store = new Store();
            var root = store.Root;
            var a = root.NewId();
            var b = root.NewId();
            root.Union(b, a);
            Assert.False(root.IsClean);
            var child = root.CreateChild();
            Assert.True(root.IsClean);
            Assert.True(child.IsClean);
            Assert.Equal(a, child.Find(b));
        }

        [Fact]
        public void AnExceptionInAChildsWriteFaultsIt()
        {
            var store = new Store();
            var f = store.AddTable(StoreTests.Function(store), [], [store.UnionFindTable], "f");
            var child = store.Root.CreateChild();

            // An exception escaping the batch, after something was staged, faults the child (X12).
            Assert.Throws<ArgumentException>(() => child.Write<int>(s =>
            {
                s.StageInsert(f, [1, 2, 3]);
                throw new ArgumentException("boom");
            }));
            Assert.True(child.IsFaulted);
            Assert.False(child.IsClean);
            Assert.Throws<InvalidOperationException>(() => child.Union(1, 2));
            Assert.Throws<InvalidOperationException>(() => child.Rebuild());
            Assert.Throws<InvalidOperationException>(() => child.Find(1));
            child.Dispose();

            // The root is never faulted: it keeps the database's own guarantee.
            Assert.Throws<ArgumentException>(() => store.Root.Write<int>(s => throw new ArgumentException("boom")));
            Assert.False(store.Root.IsFaulted);
        }

        [Fact]
        public void UnionsInAChildRejectOutOfOrderTimestampsAndFaultIt()
        {
            var store = new Store();
            var child = store.Root.CreateChild();
            child.Write(s =>
            {
                s.StageInsert(store.UnionFindTable, [1, 2, s.Timestamp + 5]);
                return 0;
            });
            Assert.Throws<InvalidOperationException>(() => child.Write(s =>
            {
                s.StageInsert(store.UnionFindTable, [3, 4, s.Timestamp]);
                return 0;
            }));
            Assert.True(child.IsFaulted);
        }

        [Fact]
        public void FreezeNeedsAFrozenParentAndFreezesTheUnionFind()
        {
            var store = new Store();
            var root = store.Root;
            var ids = Enumerable.Range(0, 6).Select(_ => root.NewId()).ToArray();
            var child = root.CreateChild();
            Assert.Throws<InvalidOperationException>(() => child.Freeze());

            root.Union(ids[1], ids[0]);
            root.Freeze();
            Assert.True(root.IsFrozen);
            Assert.True(store.RootUnionFind.IsFrozen);
            Assert.Throws<InvalidOperationException>(() => root.Union(ids[2], ids[3]));
            Assert.Throws<InvalidOperationException>(() => root.NewId());
            Assert.Equal(ids[0], root.Find(ids[1]));
            Assert.False(root.Rebuild());

            // The child created before the freeze catches up once and freezes on top of it.
            child.Union(ids[3], ids[1]);
            child.Freeze();
            Assert.True(child.IsFrozen);
            Assert.True(((OverlayDisplacedTable)child.UnionFindTable).IsFrozen);
            Assert.Equal(ids[0], child.Find(ids[3]));
            Assert.Throws<InvalidOperationException>(() => child.Union(ids[4], ids[5]));
            child.CheckInvariants();

            // Its children find through the flattened chain: one probe above the root.
            var grandchild = child.CreateChild();
            Assert.Empty(grandchild.UnionFind.Levels.ToArray().Where(level => ReferenceEquals(level, child.Overlay)));
            Assert.NotNull(grandchild.UnionFind.Prefix);
            Assert.Equal(ids[0], grandchild.Find(ids[3]));
            grandchild.Union(ids[5], ids[3]);
            grandchild.Rebuild();
            Assert.Equal(ids[0], grandchild.Find(ids[5]));
            grandchild.CheckInvariants();

            // Freezing again does nothing; a frozen context with live children cannot be disposed.
            child.Freeze();
            Assert.Throws<InvalidOperationException>(() => child.Dispose());
            grandchild.Dispose();
            child.Dispose();
        }

        [Fact]
        public void FreezingWithUnmergedUnionsThrows()
        {
            var store = new Store();
            var buffer = store.RootUnionFind.NewBuffer();
            buffer.StageInsert([2, 1, 1]);
            buffer.Flush();
            Assert.Throws<InvalidOperationException>(() => store.Root.Freeze());
            Assert.False(store.Root.IsFrozen);
            Assert.False(store.Root.IsFaulted);
        }

        [Fact]
        public void WritesAndRebuildsWithWorkAreRefusedInsideAWriteAction()
        {
            // §8.3, I3: a nested write, or a rebuild with work, inside a batch's action would run an epoch inside the
            // batch's or change the parent it caught up with. It is refused before it changes anything; the batch
            // completes and nothing is faulted.
            var store = new Store();
            var root = store.Root;
            var ids = Enumerable.Range(0, 8).Select(_ => root.NewId()).ToArray();
            var child = root.CreateChild();
            var sibling = root.CreateChild();
            sibling.Union(ids[5], ids[4]);
            Assert.False(sibling.IsClean);

            var result = child.Write(state =>
            {
                Assert.Throws<InvalidOperationException>(() => child.Union(ids[1], ids[0]));
                Assert.Throws<InvalidOperationException>(() => root.Union(ids[1], ids[0]));
                Assert.Throws<InvalidOperationException>(() => child.Write(_ => 0));
                Assert.Throws<InvalidOperationException>(() => sibling.Rebuild());
                Assert.Throws<InvalidOperationException>(() => child.CreateChild());
                Assert.Throws<InvalidOperationException>(() => sibling.Dispose());

                // Reads, and rebuilds with nothing to do, are allowed.
                Assert.False(root.Rebuild());
                Assert.False(child.Rebuild());
                Assert.Equal(ids[2], root.Find(ids[2]));
                Assert.Equal(ids[2], child.Find(ids[2]));

                state.StageInsert(store.UnionFindTable, [ids[3], ids[2], state.Timestamp]);
                return 7;
            });

            Assert.Equal(7, result);
            Assert.False(child.IsFaulted);
            Assert.False(sibling.IsFaulted);
            Assert.False(sibling.IsDisposed);
            Assert.Equal(0, root.DisplacedCount);
            child.Rebuild();
            Assert.Equal(ids[2], child.Find(ids[3]));
            Assert.False(child.AreEqual(ids[0], ids[1]));
            child.CheckInvariants();
            sibling.Rebuild();
            Assert.Equal(ids[4], sibling.Find(ids[5]));
            sibling.CheckInvariants();
        }

        [Fact]
        public void TheRootUnionFindIsClearedOnlyWithoutLiveDescendants()
        {
            // I9: a child's cursor indexes the root's displaced ids, so they cannot shrink while it lives (§6.7).
            var store = new Store();
            var root = store.Root;
            var ids = Enumerable.Range(0, 4).Select(_ => root.NewId()).ToArray();
            root.Union(ids[1], ids[0]);
            root.Rebuild();
            var child = root.CreateChild();
            var grandchild = child.CreateChild();
            Assert.Throws<InvalidOperationException>(() => root.Database.ClearTable(store.UnionFindTable));
            grandchild.Dispose();
            Assert.Throws<InvalidOperationException>(() => root.Database.ClearTable(store.UnionFindTable));
            Assert.Equal(1, root.DisplacedCount);
            Assert.Equal(ids[0], child.Find(ids[1]));

            child.Dispose();
            root.Database.ClearTable(store.UnionFindTable);
            Assert.Equal(0, root.DisplacedCount);
        }

        [Fact]
        public void ScanMergesAndIsLocalIdAreRefusedAtTheRoot()
        {
            var store = new Store();
            var visitor = new MergeCollector();
            Assert.Throws<InvalidOperationException>(() => store.Root.ScanMerges(ref visitor));
            Assert.Throws<InvalidOperationException>(() => store.Root.IsLocalId(0));
        }

        [Fact]
        public void DisposeReleasesTheUnionFind()
        {
            var store = new Store();
            var child = store.Root.CreateChild();
            child.Union(1, 2);
            child.Dispose();
            Assert.True(child.Overlay!.IsEmpty);
            Assert.Equal(0, child.Journal!.Count);
            Assert.Throws<ObjectDisposedException>(() => child.Find(1));
            Assert.Throws<ObjectDisposedException>(() => child.Union(1, 2));
            Assert.Throws<ObjectDisposedException>(() => child.Rebuild());
            Assert.Throws<ObjectDisposedException>(() => child.Freeze());
        }

    }

}

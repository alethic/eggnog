using System;
using System.Linq;

using Xunit;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// Tests for a context's union-find API: unions, finds and the cleanliness they need, catch-up after a parent
    /// change with its statistics, own displacements, the minimal write, faulting, the union-find part of freezing and
    /// the finders it hands to children.
    /// </summary>
    public class ContextUnionFindTests
    {

        /// <summary>
        /// A root union makes the root unclean, so finds throw until a rebuild, which takes one epoch; a clean root's
        /// rebuild does nothing.
        /// </summary>
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

            // A clean root rebuilds nothing and leaves the clock alone, so its timestamps stay egglog's.
            Assert.False(root.Rebuild());
            Assert.Equal(clock + 1, store.ReadClock());
            Assert.Equal(new UnionFindStatistics(1, 0, 0, 0), root.UnionFindStatistics);
            Assert.False(root.IsOwnDisplacement(b));
        }

        /// <summary>
        /// A root rebuild rewrites a row naming the loser of a union to name the winner.
        /// </summary>
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

        /// <summary>
        /// A child's union of an id with a parent's non-canonical id merges the parent's class in the child only, and
        /// the child's own loser is its only displacement.
        /// </summary>
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

        /// <summary>
        /// Parent unions made after a child's own make the child unclean; its next rebuild imports them, re-keys its
        /// own class under the parent's new canonical id, and counts each kind of change.
        /// </summary>
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

        /// <summary>
        /// A write to a child that is behind its unrebuilt parent rebuilds both before it stages.
        /// </summary>
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

        /// <summary>
        /// Creating a child of an unclean parent rebuilds the parent first, so the child starts clean.
        /// </summary>
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

        /// <summary>
        /// An exception escaping a child's write batch faults the child, which then refuses unions, rebuilds and finds;
        /// an exception at the root leaves it unfaulted.
        /// </summary>
        [Fact]
        public void AnExceptionInAChildsWriteFaultsIt()
        {
            var store = new Store();
            var f = store.AddTable(StoreTests.Function(store), [], [store.UnionFindTable], "f");
            var child = store.Root.CreateChild();

            // An exception escaping the batch, after something was staged, faults the child: a hide whose copy was
            // never merged cannot be repaired.
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

        /// <summary>
        /// A union staged in a child with a timestamp below one the child already holds is rejected, and the child is
        /// faulted.
        /// </summary>
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

        /// <summary>
        /// A child freezes only under a frozen parent; freezing makes the union-find read-only, and the children of a
        /// frozen child find through a flattened chain.
        /// </summary>
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
            Assert.DoesNotContain(grandchild.UnionFind.Levels.ToArray(), level => ReferenceEquals(level, child.Overlay));
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

        /// <summary>
        /// Freezing a root with staged, unmerged unions throws and leaves the root writable and unfaulted.
        /// </summary>
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

        /// <summary>
        /// Inside a write batch's action, writes, rebuilds with work and structural calls are refused; reads and
        /// rebuilds with nothing to do are allowed.
        /// </summary>
        [Fact]
        public void WritesAndRebuildsWithWorkAreRefusedInsideAWriteAction()
        {
            // A nested write, or a rebuild with work, inside a batch's action would run an epoch inside the batch's or
            // change the parent it caught up with. It is refused before it changes anything; the batch completes and
            // nothing is faulted.
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

        /// <summary>
        /// Clearing the root's union-find is refused while any descendant lives, and allowed once none does.
        /// </summary>
        [Fact]
        public void TheRootUnionFindIsClearedOnlyWithoutLiveDescendants()
        {
            // A child's cursor indexes the root's displaced ids, so they cannot shrink while it lives.
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

        /// <summary>
        /// <see cref="Context.ScanMerges{TVisitor}"/> and <see cref="Context.IsLocalId"/>, which only a child answers,
        /// throw at the root.
        /// </summary>
        [Fact]
        public void ScanMergesAndIsLocalIdAreRefusedAtTheRoot()
        {
            var store = new Store();
            var visitor = new MergeCollector();
            Assert.Throws<InvalidOperationException>(() => store.Root.ScanMerges(ref visitor));
            Assert.Throws<InvalidOperationException>(() => store.Root.IsLocalId(0));
        }

        /// <summary>
        /// Disposing a child empties its overlay and journal, and its union-find members then throw
        /// <see cref="ObjectDisposedException"/>.
        /// </summary>
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

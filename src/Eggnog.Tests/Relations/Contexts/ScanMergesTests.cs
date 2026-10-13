using System;
using System.Linq;

using Xunit;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// Tests for <see cref="Context.ScanMerges{TVisitor}"/> (colored-contexts spec §5.7, L2, G1(a)): a merged class's
    /// canonical is visited paired with itself when it is a parent id; local ids are never visited; classes the parent
    /// dissolved are gone; reads, <see cref="Context.Find"/> included, are allowed inside the visitor, while writes to any
    /// context of the domain, rebuilds with work to do and structural calls are refused before they change anything
    /// (§8.3), and a frozen context cannot be disposed while it is scanned.
    /// </summary>
    public class ScanMergesTests
    {

        static (Store Store, Context Root, uint[] Ids) Setup(int count)
        {
            var store = new Store();
            var root = store.Root;
            var ids = Enumerable.Range(0, count).Select(_ => root.NewId()).ToArray();
            return (store, root, ids);
        }

        static (uint Member, uint Canonical)[] Scan(Context context)
        {
            var visitor = new MergeCollector();
            context.ScanMerges(ref visitor);
            return visitor.Pairs.ToArray();
        }

        [Fact]
        public void TheWinnerOfAMergedClassIsVisitedWithItself()
        {
            // G1(a): the class {a, b} merged in the child reports a, its canonical, paired with itself.
            var (_, root, ids) = Setup(4);
            var child = root.CreateChild();
            child.Union(ids[2], ids[1]);
            child.Union(ids[3], ids[1]);
            child.Rebuild();
            Assert.Equal(
                new[] { (ids[1], ids[1]), (ids[2], ids[1]), (ids[3], ids[1]) },
                Scan(child).OrderBy(p => p.Member).ToArray());
        }

        [Fact]
        public void TheVisitOrderIsSlotThenInsertionOrder()
        {
            var (_, root, ids) = Setup(6);
            var child = root.CreateChild();
            child.Union(ids[4], ids[3]);
            child.Union(ids[1], ids[0]);
            child.Union(ids[3], ids[5]);
            child.Rebuild();
            Assert.Equal(new[] { (ids[4], ids[3]), (ids[3], ids[3]), (ids[5], ids[3]), (ids[1], ids[0]), (ids[0], ids[0]) }, Scan(child));
        }

        [Fact]
        public void LocalIdsAreNeverVisited()
        {
            var (store, root, ids) = Setup(2);
            var child = root.CreateChild();

            // A local id above the parent's: the parent id wins and is visited alone.
            var high = child.NewId();
            Assert.True(child.IsLocalId(high));
            child.Union(high, ids[0]);

            // A local id below a parent id minted later (G2): the local id wins, and the parent id is visited with it.
            var late = root.NewId();
            Assert.True(late > high);
            child.Union(late, high);
            child.Rebuild();
            var pairs = Scan(child);
            Assert.DoesNotContain(pairs, p => child.IsLocalId(p.Member));
            Assert.Equal(new[] { (ids[0], ids[0]), (late, ids[0]) }, pairs.OrderBy(p => p.Member).ToArray());

            // In a fresh child, the local winner is reported as the canonical of the parent id it beat.
            var other = root.CreateChild();
            var local = other.NewId();
            var parentId = root.NewId();
            Assert.True(local < parentId);
            other.Union(parentId, local);
            other.Rebuild();
            Assert.Equal(new[] { (parentId, local) }, Scan(other));
            Assert.Equal(1L, other.UnionFindStatistics.ParentIdLosers);
            _ = store;
        }

        [Fact]
        public void ClassesTheParentDissolvedAreAbsent()
        {
            var (_, root, ids) = Setup(4);
            var child = root.CreateChild();
            child.Union(ids[1], ids[2]);
            child.Union(ids[0], ids[3]);
            child.Rebuild();
            Assert.Equal(4, Scan(child).Length);

            // The parent merges 1 and 2 itself: the child's class {1, 2} dissolves; {0, 3} stays.
            root.Union(ids[1], ids[2]);
            child.Rebuild();
            Assert.Equal(new[] { (ids[3], ids[0]), (ids[0], ids[0]) }.OrderBy(p => p.Item1), Scan(child).OrderBy(p => p.Member));
        }

        [Fact]
        public void ReadsAreAllowedInsideTheVisitor()
        {
            var (_, root, ids) = Setup(3);
            var child = root.CreateChild();
            child.Union(ids[1], ids[2]);
            child.Rebuild();
            var checkedMembers = 0;
            var visitor = new MergeCollector((member, canonical) =>
            {
                Assert.Equal(canonical, child.Find(member));
                Assert.True(child.AreEqual(member, canonical));
                Assert.True(child.IsClean);
                checkedMembers++;
            });
            child.ScanMerges(ref visitor);
            Assert.Equal(2, checkedMembers);
        }

        [Fact]
        public void WritesAndStructuralCallsAreRefusedInsideTheVisitor()
        {
            var (_, root, ids) = Setup(4);
            var child = root.CreateChild();
            child.Union(ids[1], ids[2]);
            child.Rebuild();

            var structural = new MergeCollector((_, _) =>
            {
                Assert.Throws<InvalidOperationException>(() => child.CreateChild());
                Assert.Throws<InvalidOperationException>(() => root.CreateChild());
                Assert.Throws<InvalidOperationException>(() => child.Freeze());
            });
            child.ScanMerges(ref structural);
            Assert.False(child.IsDisposed);

            // A write to the scanned context is refused before it stages anything: the union does not land, the scan
            // completes, and the context stays clean and usable.
            var before = child.DisplacedCount;
            var writing = new MergeCollector((_, _) =>
            {
                Assert.Throws<InvalidOperationException>(() => child.Union(ids[0], ids[3]));
                Assert.Throws<InvalidOperationException>(() => child.Write(_ => 0));
            });
            child.ScanMerges(ref writing);
            Assert.Equal(2, writing.Pairs.Count);
            Assert.Equal(before, child.DisplacedCount);
            Assert.False(child.IsFaulted);
            Assert.True(child.IsClean);
            Assert.False(child.AreEqual(ids[0], ids[3]));

            // So is a write to an ancestor that shares the domain, which would leave the scanned context unclean.
            var rootBefore = root.DisplacedCount;
            var ancestor = new MergeCollector((_, _) =>
            {
                Assert.Throws<InvalidOperationException>(() => root.Union(ids[0], ids[3]));
                Assert.True(child.IsClean);
            });
            child.ScanMerges(ref ancestor);
            Assert.Equal(2, ancestor.Pairs.Count);
            Assert.Equal(rootBefore, root.DisplacedCount);
            Assert.True(child.IsClean);
            Assert.False(root.IsFaulted);
        }

        [Fact]
        public void ARebuildWithWorkIsRefusedInsideTheVisitorAndOneWithoutIsAllowed()
        {
            var (_, root, ids) = Setup(6);
            var child = root.CreateChild();
            var sibling = root.CreateChild();
            child.Union(ids[1], ids[2]);
            child.Rebuild();

            // The sibling shares the root's domain and has a union to rebuild.
            sibling.Union(ids[4], ids[3]);
            Assert.False(sibling.IsClean);
            var visitor = new MergeCollector((_, _) =>
            {
                Assert.False(child.Rebuild());
                Assert.False(root.Rebuild());
                Assert.Throws<InvalidOperationException>(() => sibling.Rebuild());
            });
            child.ScanMerges(ref visitor);
            Assert.Equal(2, visitor.Pairs.Count);
            Assert.False(sibling.IsFaulted);
            sibling.Rebuild();
            Assert.True(sibling.IsClean);
            Assert.Equal(ids[3], sibling.Find(ids[4]));
        }

        [Fact]
        public void AFrozenContextCannotBeDisposedWhileAnotherThreadScansIt()
        {
            var (_, root, ids) = Setup(4);
            var child = root.CreateChild();
            child.Union(ids[1], ids[0]);
            child.Union(ids[3], ids[2]);
            root.Freeze();
            child.Freeze();

            using var entered = new System.Threading.ManualResetEventSlim();
            using var release = new System.Threading.ManualResetEventSlim();
            var visitor = new MergeCollector((_, _) =>
            {
                entered.Set();
                release.Wait();
            });
            var scan = System.Threading.Tasks.Task.Run(() => child.ScanMerges(ref visitor));
            entered.Wait();
            try
            {
                Assert.Throws<InvalidOperationException>(() => child.Dispose());
                Assert.False(child.IsDisposed);
            }
            finally
            {
                release.Set();
                scan.Wait();
            }

            Assert.Equal(4, visitor.Pairs.Count);
            child.Dispose();
            Assert.True(child.IsDisposed);
            var after = new MergeCollector();
            Assert.Throws<ObjectDisposedException>(() => child.ScanMerges(ref after));
        }

        [Fact]
        public void ScanMergesNeedsACleanContext()
        {
            var (_, root, ids) = Setup(3);
            var child = root.CreateChild();
            child.Union(ids[1], ids[2]);
            var visitor = new MergeCollector();
            Assert.Throws<InvalidOperationException>(() => child.ScanMerges(ref visitor));
            child.Rebuild();
            root.Union(ids[0], ids[1]);
            Assert.Throws<InvalidOperationException>(() => child.ScanMerges(ref visitor));
            child.Rebuild();
            child.ScanMerges(ref visitor);
            Assert.NotEmpty(visitor.Pairs);
        }

        [Fact]
        public void AFrozenContextIsScannedWithoutItsDomain()
        {
            var (_, root, ids) = Setup(3);
            var child = root.CreateChild();
            child.Union(ids[1], ids[2]);
            root.Freeze();
            child.Freeze();
            Assert.Equal(2, Scan(child).Length);
        }

    }

}

using System;
using System.Linq;
using System.Threading.Tasks;

using Xunit;

using static Eggnog.Relations.Tests.LayeredTestUtil;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// Tests for <see cref="Context.Dispose"/> in full: the rules (the root and a context with live children refuse, a
    /// second call does nothing, every later call throws <see cref="ObjectDisposedException"/>), what it releases, the
    /// gates it exits (which release compaction and retraction above it), the container dirty log cleared with the last
    /// descendant, and disposing faulted, frozen and scanned contexts.
    /// </summary>
    public class DisposeTests
    {

        /// <summary>
        /// A second dispose does nothing; every later call to another member throws
        /// <see cref="ObjectDisposedException"/>.
        /// </summary>
        [Fact]
        public void EveryLaterCallThrowsObjectDisposed()
        {
            var store = new Store(ParallelHeuristics.Serial);
            var f = store.AddTable(MaxFunction(1), name: "f");
            var root = store.Root;
            var a = root.NewId();
            var child = root.CreateChild();
            Insert(child, f, a, 1, 0);
            child.Dispose();
            Assert.True(child.IsDisposed);
            child.Dispose();

            var sink = new RowCollector();
            Assert.Throws<ObjectDisposedException>(() => child.Find(a));
            Assert.Throws<ObjectDisposedException>(() => child.Union(a, a));
            Assert.Throws<ObjectDisposedException>(() => child.Rebuild());
            Assert.Throws<ObjectDisposedException>(() => child.Freeze());
            Assert.Throws<ObjectDisposedException>(() => child.CreateChild());
            Assert.Throws<ObjectDisposedException>(() => child.GetView(f));
            Assert.Throws<ObjectDisposedException>(() => Row(child, f, a));
            Assert.Throws<ObjectDisposedException>(() => Insert(child, f, a, 2, 0));
            Assert.Throws<ObjectDisposedException>(() => child.ClearTable(f));
            Assert.Throws<ObjectDisposedException>(() => child.NewId());
            Assert.Throws<ObjectDisposedException>(() => child.ScanOwn(f, ref sink));
            Assert.Throws<ObjectDisposedException>(() => child.ScanAncestorsMentioning(f, a, ref sink));
            Assert.Throws<ObjectDisposedException>(() => child.IsContradictory);
            Assert.Throws<ObjectDisposedException>(() => child.CheckInvariants());
        }

        /// <summary>
        /// The root is never disposed, and a context with live children is not until they are.
        /// </summary>
        [Fact]
        public void TheRootAndAParentWithLiveChildrenRefuse()
        {
            var store = new Store(ParallelHeuristics.Serial);
            var root = store.Root;
            var child = root.CreateChild();
            var grandchild = child.CreateChild();
            Assert.Throws<InvalidOperationException>(() => root.Dispose());
            Assert.Throws<InvalidOperationException>(() => child.Dispose());
            Assert.False(child.IsDisposed);
            grandchild.Dispose();
            child.Dispose();
            Assert.Equal(0, root.ChildCount);
        }

        /// <summary>
        /// Disposing releases the context's structures and host, and exits every ancestor's gate, which lets the root
        /// retract and compact again.
        /// </summary>
        [Fact]
        public void DisposeReleasesAndExitsTheGates()
        {
            var store = new Store(ParallelHeuristics.Serial);
            var f = store.AddTable(MaxFunction(1), name: "f");
            var root = store.Root;
            var a = root.NewId();
            Insert(root, f, a, 1, 0);
            var child = root.CreateChild();
            var grandchild = child.CreateChild();
            grandchild.Host = new object();
            Insert(grandchild, f, a, 2, 0);
            Assert.Equal(2, root.Gate.LiveDescendants);
            Assert.Equal(1, child.Gate.LiveDescendants);
            Assert.Throws<InvalidOperationException>(() => Remove(root, f, a));

            grandchild.Dispose();
            Assert.Null(grandchild.Host);
            Assert.Null(grandchild.Tables!.TryGetLayer(f));
            Assert.Equal(1, root.Gate.LiveDescendants);
            Assert.Equal(0, child.Gate.LiveDescendants);
            child.Dispose();
            Assert.Equal(0, root.Gate.LiveDescendants);

            // The root may retract again.
            Remove(root, f, a);
            Assert.Null(Row(root, f, a));
        }

        /// <summary>
        /// A faulted context is disposed like any other; so is a frozen one, while its siblings live.
        /// </summary>
        [Fact]
        public void FaultedAndFrozenContextsAreDisposed()
        {
            var store = new Store(ParallelHeuristics.Serial);
            var f = store.AddTable(MaxFunction(1), name: "f");
            var root = store.Root;
            var a = root.NewId();
            var faulted = root.CreateChild();
            Assert.Throws<InvalidOperationException>(() => Remove(faulted, f, a));
            Assert.True(faulted.IsFaulted);
            Assert.Throws<InvalidOperationException>(() => faulted.Rebuild());
            faulted.Dispose();

            root.Freeze();
            var frozen = root.CreateChild();
            var sibling = root.CreateChild();
            Insert(frozen, f, a, 3, 0);
            frozen.Freeze();
            frozen.Dispose();
            Insert(sibling, f, a, 4, 0);
            Assert.Equal(4u, Row(sibling, f, a)![1]);
            sibling.Dispose();
        }

        /// <summary>
        /// A dispose from inside a scan of the context (or from another thread during one) is refused; a re-entrant
        /// dispose from a write batch too.
        /// </summary>
        [Fact]
        public void ADisposeDuringAScanOrABatchIsRefused()
        {
            var store = new Store(ParallelHeuristics.Serial);
            var f = store.AddTable(MaxFunction(1), name: "f");
            var root = store.Root;
            var a = root.NewId();
            Insert(root, f, a, 1, 0);
            root.Freeze();
            var child = root.CreateChild();
            Insert(child, f, a, 2, 0);
            child.Freeze();

            var sink = new DisposingSink(child);
            child.ScanOwn(f, ref sink);
            Assert.IsType<InvalidOperationException>(sink.Error);
            Assert.False(child.IsDisposed);

            var sibling = root.CreateChild();
            Assert.Throws<InvalidOperationException>(() => sibling.Write(state =>
            {
                sibling.Dispose();
                return 0;
            }));
            sibling.Dispose();
            child.Dispose();
        }

        /// <summary>
        /// The container dirty log is cleared when the root's last descendant goes, from whichever thread disposes it.
        /// </summary>
        [Fact]
        public void TheDirtyLogIsClearedWithTheLastDescendant()
        {
            var store = new Store(ParallelHeuristics.Serial);
            var root = store.Root;
            root.Freeze();
            var children = Enumerable.Range(0, 4).Select(_ => root.CreateChild()).ToArray();
            store.ContainerDirtyLog.Append([1, 2, 3]);
            Parallel.ForEach(children.Take(3), child => child.Dispose());
            Assert.Equal(3, store.ContainerDirtyLog.End);
            children[3].Dispose();
            Assert.Equal(0, store.ContainerDirtyLog.End);
        }

    }

}

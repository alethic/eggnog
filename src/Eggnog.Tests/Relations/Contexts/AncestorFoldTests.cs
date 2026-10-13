using System;

using Xunit;

using static Eggnog.Relations.Tests.LayeredTestUtil;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// A parent's changes that fold rows: a root rebuild that folds a rewritten row into an equal row only tombstones,
    /// and a union the root has not rebuilt yet folds rows only in its child's view. The child sees what a copy would,
    /// and the root's rows stay as they are.
    /// </summary>
    [Collection(AllocationProbeCollection.Name)]
    public class AncestorFoldTests
    {

        /// <summary>
        /// A root rebuild that folds a rewritten row into an equal row only tombstones; the child sees it as a copy
        /// would, with and without an own row naming the loser.
        /// </summary>
        [Fact]
        public void AnAncestorFoldThatOnlyTombstonesIsSeenAsItIs()
        {
            var store = new Store(ParallelHeuristics.Serial);
            var f = store.AddTable(MaxFunction(1), name: "f");
            var root = store.Root;
            var a = root.NewId();
            var b = root.NewId();
            var c = root.NewId();
            var d = root.NewId();
            Insert(root, f, a, 1, 0);
            Insert(root, f, b, 1, 0);
            Insert(root, f, c, 1, 0);
            Insert(root, f, d, 1, 0);
            var plain = root.CreateChild();
            var holder = root.CreateChild();
            Insert(holder, f, d, 5, 0);
            root.Union(a, b);
            root.Union(c, d);
            root.Rebuild();
            plain.Rebuild();
            holder.Rebuild();
            plain.CheckInvariants();
            holder.CheckInvariants();
            Assert.Equal(a, plain.Find(b));
            Assert.Equal(1u, Row(plain, f, a)![1]);
            Assert.Equal(1u, Row(plain, f, c)![1]);
            Assert.Equal(5u, Row(holder, f, c)![1]);
            Assert.Equal(1u, Row(holder, f, a)![1]);
            LookupPaths.Check(plain);
            LookupPaths.Check(holder);
        }

        /// <summary>
        /// The root unions a and b after its child exists, and does not rebuild: the child's rebuild folds <c>f(b) =
        /// 2</c> into <c>f(a) = 5</c> in its own view, as a copy would, and leaves the root's layer and rows unchanged.
        /// </summary>
        [Fact]
        public void AChildFoldsAnUnrebuiltParentUnionWithoutChangingTheParent()
        {
            var store = new Store(ParallelHeuristics.Serial);
            var f = store.AddTable(MaxFunction(1), name: "f");
            var root = store.Root;
            var a = root.NewId();
            var b = root.NewId();
            Insert(root, f, a, 5, 0);
            Insert(root, f, b, 2, 0);
            var child = root.CreateChild();
            var version = store.Schema[f]!.Root.Version;

            root.Union(a, b);
            child.Rebuild();
            Assert.Equal(version, store.Schema[f]!.Root.Version);
            Assert.True(child.IsClean);
            Assert.Equal(5u, Row(child, f, a)![1]);
            Assert.Null(Row(child, f, b));
            Assert.Equal(5u, Row(root, f, a)![1]);
            LookupPaths.Check(child);
        }

    }

}

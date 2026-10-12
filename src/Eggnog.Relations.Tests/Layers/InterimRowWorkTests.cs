using System;

using Xunit;

using static Eggnog.Relations.Tests.LayeredTestUtil;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// Tests for the M9.3 interim refusal of a child's row work (colored-contexts spec §17 M9.3, §7.2, §7.3): a rebuild that
    /// would need row catch-up, a rewrite of the child's own rows or a hide-and-copy of ancestor rows naming its losers is
    /// refused before anything changes, and the child is not faulted; a rebuild that needs none of it catches up, and the
    /// child is then a copy.
    /// </summary>
    public class InterimRowWorkTests
    {

        /// <summary>
        /// A child's own union whose loser its own row names would need step 3 (the rewrite of own rows).
        /// </summary>
        [Fact]
        public void AnOwnUnionOverAnOwnRowIsRefused()
        {
            var store = new Store(ParallelHeuristics.Serial);
            var f = store.AddTable(MaxFunction(1), name: "f");
            var root = store.Root;
            var a = root.NewId();
            var b = root.NewId();
            root.Freeze();
            var child = root.CreateChild();
            Insert(child, f, b, 1, 0);
            child.Union(a, b);

            Assert.Throws<NotSupportedException>(() => child.Rebuild());
            Assert.False(child.IsFaulted);
            Assert.False(child.IsClean);
            Assert.Throws<NotSupportedException>(() => child.Freeze());
            Assert.False(child.IsFaulted);
        }

        /// <summary>
        /// A child's own union whose loser a visible ancestor row names would need step 4 (hide and copy).
        /// </summary>
        [Fact]
        public void AnOwnUnionOverAVisibleAncestorRowIsRefused()
        {
            var store = new Store(ParallelHeuristics.Serial);
            var f = store.AddTable(MaxFunction(1), name: "f");
            var root = store.Root;
            var a = root.NewId();
            var b = root.NewId();
            Insert(root, f, b, 2, 0);
            root.Freeze();
            var child = root.CreateChild();
            child.Union(a, b);

            Assert.Throws<NotSupportedException>(() => child.Rebuild());
            Assert.False(child.IsFaulted);
        }

        /// <summary>
        /// A child's own union that no row it holds or sees names needs no row work: the child is clean, its view is its
        /// parent's, and the ancestor row it hid on account of its own row does not count.
        /// </summary>
        [Fact]
        public void AnOwnUnionNoVisibleRowNamesCatchesUp()
        {
            var store = new Store(ParallelHeuristics.Serial);
            var f = store.AddTable(MaxFunction(1), name: "f");
            var root = store.Root;
            var a = root.NewId();
            var b = root.NewId();
            var c = root.NewId();
            Insert(root, f, c, 2, 0);
            Insert(root, f, b, 1, 0);
            root.Freeze();
            var child = root.CreateChild();

            // The child's own f(b) = 3 hides the root's f(b) = 1 (KeyHeld); then it deletes its row, so nothing it sees names b.
            Insert(child, f, b, 3, 0);
            Remove(child, f, b);
            child.Union(a, b);

            child.Rebuild();
            Assert.True(child.IsClean);
            Assert.Equal(a, child.Find(b));
            Assert.Equal(2u, Row(child, f, c)![1]);
            Assert.Null(Row(child, f, b));
            Assert.Null(Row(child, f, a));
            LookupPaths.Check(child);
        }

        /// <summary>
        /// An import of a parent displacement that no own row names needs no row work, even with own rows present.
        /// </summary>
        [Fact]
        public void AnImportNoOwnRowNamesCatchesUp()
        {
            var store = new Store(ParallelHeuristics.Serial);
            var f = store.AddTable(MaxFunction(1), name: "f");
            var root = store.Root;
            var a = root.NewId();
            var b = root.NewId();
            var c = root.NewId();
            var child = root.CreateChild();
            Insert(child, f, c, 1, 0);
            root.Union(a, b);

            child.Rebuild();
            Assert.True(child.IsClean);
            Assert.Equal(a, child.Find(b));
            Assert.Equal(1u, Row(child, f, c)![1]);
            LookupPaths.Check(child);
        }

        /// <summary>
        /// A parent's rebuild that folds a rewritten row into an existing row tombstones without appending (its layer's
        /// version does not move); a child holding nothing the union makes stale sees the tombstone as it is, which is what
        /// a copy sees.
        /// </summary>
        [Fact]
        public void AnAncestorFoldThatOnlyTombstonesIsSeenAsItIs()
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

        /// <summary>
        /// A parent that appends rows while its child lives needs row catch-up (M9.4): refused without faulting.
        /// </summary>
        [Fact]
        public void AncestorAppendsAreRefused()
        {
            var store = new Store(ParallelHeuristics.Serial);
            var f = store.AddTable(MaxFunction(1), name: "f");
            var root = store.Root;
            var a = root.NewId();
            var child = root.CreateChild();
            Insert(root, f, a, 5, 0);

            Assert.Throws<NotSupportedException>(() => child.Rebuild());
            Assert.Throws<NotSupportedException>(() => Insert(child, f, a, 6, 0));
            Assert.False(child.IsFaulted);
        }

    }

}

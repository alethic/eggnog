using System.Linq;

using Xunit;

using static Eggnog.Relations.Tests.LayeredTestUtil;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// Tests for the root's settle: raw root writes are egglog's (stored as given, rewritten by the next rebuild), and
    /// before the root is shared, by a child or a freeze, the rows written since its last rebuild are canonicalized; a
    /// stale root append after a child exists is copied and hidden by the child's catch-up instead. A root program that
    /// creates no child never settles.
    /// </summary>
    public class RootSettleTests
    {

        /// <summary>
        /// A raw root write of a stale id, then <see cref="Context.CreateChild"/>: the settle rewrites the row, so the
        /// child's view starts canonical.
        /// </summary>
        [Fact]
        public void CreatingAChildSettlesAStaleRootWrite()
        {
            var store = new Store(ParallelHeuristics.Serial);
            var f = store.AddTable(MaxFunction(1), name: "f");
            var root = store.Root;
            var a = root.NewId();
            var b = root.NewId();
            root.Union(a, b);
            root.Rebuild();

            // egglog's raw write: stored as given, under the stale key.
            Insert(root, f, b, 4, 0);
            Assert.Equal(4u, Row(root, f, b)![1]);
            Assert.True(store.Schema[f]!.Root.HasRowsSinceRebuild(store.UnionFindTable));

            var child = root.CreateChild();
            Assert.False(store.Schema[f]!.Root.HasRowsSinceRebuild(store.UnionFindTable));
            Assert.Equal(4u, Row(root, f, a)![1]);
            Assert.Null(Row(root, f, b));
            Assert.Equal(4u, Row(child, f, a)![1]);
            Assert.Null(Row(child, f, b));
            root.CheckRootSettled();
            child.CheckInvariants();
        }

        /// <summary>
        /// The settle's merge may stage unions (two rewritten rows of a constructor meet), which its rebuild applies.
        /// </summary>
        [Fact]
        public void ASettleThatMeetsRowsRebuilds()
        {
            var store = new Store(ParallelHeuristics.Serial);
            var g = store.AddTable(Constructor(store), [], [store.UnionFindTable], "g");
            var root = store.Root;
            var ids = Enumerable.Range(0, 4).Select(_ => root.NewId()).ToArray();
            root.Union(ids[0], ids[1]);
            root.Rebuild();
            Insert(root, g, ids[0], ids[2], 0);
            Insert(root, g, ids[1], ids[3], 0);
            Assert.NotEqual(root.Find(ids[2]), root.Find(ids[3]));

            var child = root.CreateChild();
            Assert.Equal(root.Find(ids[2]), root.Find(ids[3]));
            Assert.Equal(child.Find(ids[2]), child.Find(ids[3]));
            Assert.Equal(1, child.GetView(g).VisibleCount);
            root.CheckRootSettled();
            Assert.True(root.IsClean);
        }

        /// <summary>
        /// A stale root append after the child exists is not settled (the root is not shared anew); the child's
        /// catch-up copies it canonically and hides it, since it names an id that is not canonical in the child.
        /// </summary>
        [Fact]
        public void AStaleRootAppendAfterCreationIsCopiedByCatchUp()
        {
            var store = new Store(ParallelHeuristics.Serial);
            var f = store.AddTable(MaxFunction(1), name: "f");
            var root = store.Root;
            var a = root.NewId();
            var b = root.NewId();
            root.Union(a, b);
            root.Rebuild();
            var child = root.CreateChild();

            Insert(root, f, b, 4, 0);
            Assert.Equal(4u, Row(root, f, b)![1]);
            child.Rebuild();
            Assert.Equal(4u, Row(child, f, a)![1]);
            Assert.Null(Row(child, f, b));
            Assert.Equal(1, child.Tables!.Statistics.RowsCopied);
            Assert.Equal(HideReason.Mentions, child.Tables.TryGetLayer(f)!.ReasonFor(0, 0) ?? HideReason.Mentions);
            child.CheckInvariants();
        }

        /// <summary>
        /// <see cref="Context.Freeze"/> settles the root too.
        /// </summary>
        [Fact]
        public void FreezingSettles()
        {
            var store = new Store(ParallelHeuristics.Serial);
            var f = store.AddTable(MaxFunction(1), name: "f");
            var root = store.Root;
            var a = root.NewId();
            var b = root.NewId();
            root.Union(a, b);
            root.Rebuild();
            Insert(root, f, b, 4, 0);
            root.Freeze();
            Assert.Equal(4u, Row(root, f, a)![1]);
            Assert.Null(Row(root, f, b));
            root.CheckRootSettled();
        }

        /// <summary>
        /// A root program that creates no child and does not freeze never settles: its stale raw row stays as written
        /// and its clock advances only for its own writes and rebuild passes, as egglog's.
        /// </summary>
        [Fact]
        public void ARootThatCreatesNoChildNeverSettles()
        {
            var store = new Store(ParallelHeuristics.Serial);
            var f = store.AddTable(MaxFunction(1), name: "f");
            var root = store.Root;
            var a = root.NewId();
            var b = root.NewId();
            var clock = store.ReadClock();
            root.Union(a, b);
            root.Rebuild();
            Insert(root, f, b, 4, 0);
            root.Rebuild();

            // One epoch for the union's write, one for the rebuild's single pass, one for the row's write; the second
            // rebuild has nothing to do (no union since the last one, no pending write) and takes no epoch.
            Assert.Equal(clock + 3, store.ReadClock());
            Assert.Equal(4u, Row(root, f, b)![1]);
            Assert.True(store.Schema[f]!.Root.HasRowsSinceRebuild(store.UnionFindTable));
        }

        /// <summary>
        /// A root with nothing written since its last rebuild settles without an epoch.
        /// </summary>
        [Fact]
        public void ASettledRootTakesNoEpochToSettleAgain()
        {
            var store = new Store(ParallelHeuristics.Serial);
            var f = store.AddTable(MaxFunction(1), name: "f");
            var root = store.Root;
            var a = root.NewId();
            Insert(root, f, a, 1, 0);
            root.CreateChild().Dispose();
            var clock = store.ReadClock();
            var child = root.CreateChild();
            Assert.Equal(clock, store.ReadClock());
            Assert.Equal(clock, child.CreatedAt);
        }

    }

}

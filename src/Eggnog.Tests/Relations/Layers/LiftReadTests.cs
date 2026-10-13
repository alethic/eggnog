using System;
using System.Linq;

using Xunit;

using static Eggnog.Relations.Tests.LayeredTestUtil;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// Tests for the lift's storage reads: <see cref="Context.ScanOwn{TSink}"/> visits the child's own live rows, and
    /// <see cref="Context.ScanAncestorsMentioning{TSink}"/> the ancestor rows the child sees that name an id, through
    /// the ancestors' rebuild indexes; both refuse the root and an unclean context, and the second returns nothing for
    /// a table without rebuild columns, a context-private table and a local id.
    /// </summary>
    public class LiftReadTests
    {

        /// <summary>
        /// With losers-only marking, a fact that holds in a case only through an ancestor row naming a class's winner
        /// is never copied into the case, so the lift needs the winner probe, which finds it. The parent holds
        /// <c>r(a)</c>; the case unions a and b (a wins), so <c>r(b)</c> holds in the case, carried only by the
        /// parent's row <c>r(a)</c>.
        /// </summary>
        [Fact]
        public void TheWinnerProbeFindsAFactCarriedByAnAncestorRow()
        {
            var store = new Store(ParallelHeuristics.Serial);
            var r = store.AddTable(Relation(1), name: "r");
            var root = store.Root;
            var a = root.NewId();
            var b = root.NewId();
            Insert(root, r, a, 0);
            root.Freeze();

            var caseChild = root.CreateChild(ContextOptions.Case);
            caseChild.Union(a, b);
            caseChild.Rebuild();
            Assert.Equal(a, caseChild.Find(b));

            // Nothing was copied: the row names the winner only.
            var own = new RowCollector();
            caseChild.ScanOwn(r, ref own);
            Assert.Empty(own.Rows);
            Assert.Equal(0, caseChild.Tables!.Statistics.RowsCopied);

            // The winner probe finds it.
            var found = new RowCollector();
            caseChild.ScanAncestorsMentioning(r, caseChild.Find(b), ref found);
            var row = Assert.Single(found.Rows);
            Assert.Equal(a, row.Row[0]);
            Assert.NotNull(Row(caseChild, r, caseChild.Find(b)));
        }

        /// <summary>
        /// <see cref="Context.ScanOwn{TSink}"/> visits exactly the live rows the child stored, canonical in it.
        /// </summary>
        [Fact]
        public void ScanOwnVisitsTheOwnLiveRows()
        {
            var store = new Store(ParallelHeuristics.Serial);
            var f = store.AddTable(MaxFunction(1), name: "f");
            var root = store.Root;
            var ids = Enumerable.Range(0, 4).Select(_ => root.NewId()).ToArray();
            Insert(root, f, ids[0], 1, 0);
            root.Freeze();

            var child = root.CreateChild();
            Insert(child, f, ids[1], 2, 0);
            Insert(child, f, ids[3], 3, 0);
            Insert(child, f, ids[1], 4, 0);
            child.Union(ids[2], ids[3]);
            child.Rebuild();

            var own = new RowCollector();
            child.ScanOwn(f, ref own);
            var rows = own.Rows.Select(row => (row.Row[0], row.Row[1])).OrderBy(row => row.Item1).ToArray();
            Assert.Equal(new[] { (ids[1], 4u), (ids[2], 3u) }, rows);

            // A table the child never touched has no own rows.
            var g = store.AddTable(MaxFunction(1), name: "g");
            var none = new RowCollector();
            child.ScanOwn(g, ref none);
            Assert.Empty(none.Rows);
        }

        /// <summary>
        /// The ancestor scan delivers each visible ancestor row naming the id once, layer by layer, and skips rows the child
        /// hides; a two-level chain finds rows of both ancestors.
        /// </summary>
        [Fact]
        public void ScanAncestorsMentioningSkipsHiddenRowsAndCoversEveryAncestor()
        {
            var store = new Store(ParallelHeuristics.Serial);
            var g = store.AddTable(Relation(2), name: "g");
            var root = store.Root;
            var ids = Enumerable.Range(0, 5).Select(_ => root.NewId()).ToArray();
            Insert(root, g, ids[0], ids[0], 0);
            Insert(root, g, ids[0], ids[1], 0);
            Insert(root, g, ids[2], ids[0], 0);
            root.Freeze();
            var parent = root.CreateChild();
            Insert(parent, g, ids[3], ids[0], 0);
            parent.Freeze();

            var child = parent.CreateChild();

            // The child deletes one of the root's rows naming ids[0] (allowed: its parent is frozen).
            Remove(child, g, ids[2], ids[0]);
            var found = new RowCollector();
            child.ScanAncestorsMentioning(g, ids[0], ref found);
            var rows = found.Rows.Select(row => (row.Row[0], row.Row[1])).ToArray();
            Assert.Equal(new[] { (ids[0], ids[0]), (ids[0], ids[1]), (ids[3], ids[0]) }, rows);
        }

        /// <summary>
        /// Nothing for a table without rebuild columns, a context-private table or an id local to the child.
        /// </summary>
        [Fact]
        public void ScanAncestorsMentioningReturnsNothingWhereNoAncestorRowCanNameTheId()
        {
            var store = new Store(ParallelHeuristics.Serial);
            var values = store.AddTable(ValueOnly(), name: "values");
            var p = store.AddTable(MaxFunction(1), name: "p", contextPrivate: true);
            var f = store.AddTable(MaxFunction(1), name: "f");
            var root = store.Root;
            var a = root.NewId();
            Insert(root, values, 7, 1);
            Insert(root, p, a, 1, 0);
            Insert(root, f, a, 1, 0);
            root.Freeze();

            var child = root.CreateChild();
            var local = child.NewId();
            var sink = new RowCollector();
            child.ScanAncestorsMentioning(values, 7, ref sink);
            child.ScanAncestorsMentioning(p, a, ref sink);
            child.ScanAncestorsMentioning(f, local, ref sink);
            Assert.Empty(sink.Rows);
            child.ScanAncestorsMentioning(f, a, ref sink);
            Assert.Single(sink.Rows);
        }

        /// <summary>
        /// Both reads refuse the root and a context that is not clean.
        /// </summary>
        [Fact]
        public void TheReadsRefuseTheRootAndUncleanContexts()
        {
            var store = new Store(ParallelHeuristics.Serial);
            var f = store.AddTable(MaxFunction(1), name: "f");
            var root = store.Root;
            var a = root.NewId();
            var b = root.NewId();
            var sink = new RowCollector();
            Assert.Throws<InvalidOperationException>(() => root.ScanOwn(f, ref sink));
            Assert.Throws<InvalidOperationException>(() => root.ScanAncestorsMentioning(f, a, ref sink));

            var child = root.CreateChild();
            child.Union(a, b);
            Assert.Throws<InvalidOperationException>(() => child.ScanOwn(f, ref sink));
            Assert.Throws<InvalidOperationException>(() => child.ScanAncestorsMentioning(f, a, ref sink));
            child.Rebuild();
            child.ScanOwn(f, ref sink);
        }

    }

}

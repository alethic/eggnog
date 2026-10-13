using System;

using Xunit;

using static Eggnog.Relations.Tests.LayeredTestUtil;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// The tables checker's parts that <see cref="CheckInvariantsCatchesViolations"/> leaves untested (colored-contexts
    /// spec §12.7 item 2): I19 (own timestamps nondecreasing and at least the creation stamp), I15 (Lemma F) and the
    /// ancestor half of I13 (no visible ancestor row names an own displacement). Each corrupts one invariant through
    /// internal members and asserts the checker names it; a checker that dropped the part would pass every other test.
    /// </summary>
    public class CheckInvariantsCatchesMoreViolationsReviewTests
    {

        static void AssertViolates(Context context, string invariant)
        {
            var e = Assert.Throws<InvalidOperationException>(context.CheckInvariants);
            Assert.Contains(invariant + ":", e.Message);
        }

        /// <summary>
        /// I19: an own row whose timestamp lies below the context's creation stamp.
        /// </summary>
        [Fact]
        public void AnOwnRowStampedBeforeTheContextViolatesI19()
        {
            var store = new Store(ParallelHeuristics.Serial);
            var f = store.AddTable(MaxFunction(1), name: "f");
            var root = store.Root;
            var a = root.NewId();
            root.Freeze();
            var child = root.CreateChild();
            Insert(child, f, a, 5, 0);
            child.CheckInvariants();

            var own = child.Tables!.TryGetLayer(f)!.Own;
            var sort = store.Schema[f]!.SortColumn;
            Assert.True(child.CreatedAt > 0);
            own.Rows.Data[sort] = 0;
            AssertViolates(child, "I19");
        }

        /// <summary>
        /// I13, ancestor half: the context displaced an id and was marked rebuilt without step 4, so the ancestor row
        /// naming the loser stays visible.
        /// </summary>
        [Fact]
        public void AVisibleAncestorRowNamingAnOwnLoserViolatesI13()
        {
            var store = new Store(ParallelHeuristics.Serial);
            var f = store.AddTable(MaxFunction(1), name: "f");
            var root = store.Root;
            var a = root.NewId();
            var b = root.NewId();
            Insert(root, f, b, 1, 0);
            root.Freeze();
            var child = root.CreateChild();
            child.Union(a, b);
            child.JournalMark = child.Journal!.Count;
            Assert.True(child.IsClean);
            AssertViolates(child, "I13");
        }

        /// <summary>
        /// I15: a grandchild hides its parent's row while a row with the same key stays visible below it (here the root's,
        /// which the parent never hid because its cursor skipped the catch-up).
        /// </summary>
        [Fact]
        public void AHiddenRowWhoseKeyIsVisibleBelowViolatesI15()
        {
            var store = new Store(ParallelHeuristics.Serial);
            var f = store.AddTable(MaxFunction(1), name: "f");
            var root = store.Root;
            var b = root.NewId();
            var child = root.CreateChild();
            Insert(child, f, b, 5, 0);
            Insert(root, f, b, 3, 0);

            // The child skips the catch-up that would fold the root's row in (branch (b)), so both rows stay visible.
            var tables = child.Tables!;
            var cursor = tables.CursorAt(0)!;
            cursor.SetPendingRows(f, tables.AncestorLayer(0, f)!.Version);
            cursor.SetPendingStamp(root.Version);
            cursor.CommitPending();
            Assert.True(child.IsClean);

            var grandchild = child.CreateChild();
            var row = tables.TryGetLayer(f)!.Own.FindRow([b]);
            Assert.True(row >= 0);
            grandchild.Tables!.GetOrCreateLayer(f).Hide(1, row, HideReason.KeyHeld);
            grandchild.Tables.SealHidden();
            AssertViolates(grandchild, "I15");
        }

    }

}

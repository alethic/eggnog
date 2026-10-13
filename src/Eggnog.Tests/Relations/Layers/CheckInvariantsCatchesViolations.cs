using System;

using Xunit;

using static Eggnog.Relations.Tests.LayeredTestUtil;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// The tables part of the context checker, and its eq-container part, catch violations: each test corrupts one
    /// invariant deliberately, through internal members no correct operation uses that way, and asserts that
    /// <see cref="Context.CheckInvariants"/> reports it under the checker's label for that invariant.
    /// </summary>
    public class CheckInvariantsCatchesViolations
    {

        /// <summary>
        /// Builds a store with <c>f(id) → value</c> and a root holding <c>f(a) = 1</c>.
        /// </summary>
        static (Store Store, TableId F, uint A, uint B) Setup(bool freeze)
        {
            var store = new Store(ParallelHeuristics.Serial);
            var f = store.AddTable(MaxFunction(1), name: "f");
            var root = store.Root;
            var a = root.NewId();
            var b = root.NewId();
            Insert(root, f, a, 1, 0);
            if (freeze)
                root.Freeze();

            return (store, f, a, b);
        }

        /// <summary>
        /// Asserts that the checker throws naming <paramref name="invariant"/>.
        /// </summary>
        static void AssertViolates(Context context, string invariant)
        {
            var e = Assert.Throws<InvalidOperationException>(context.CheckInvariants);
            Assert.Contains(invariant + ":", e.Message);
        }

        /// <summary>
        /// An own row names an id the context displaced, and the context was marked rebuilt without rewriting it: a
        /// clean context's visible rows name canonical ids only.
        /// </summary>
        [Fact]
        public void AStaleOwnRowBreaksCanonicalIds()
        {
            var (store, f, a, b) = Setup(freeze: true);
            var child = store.Root.CreateChild();
            Insert(child, f, b, 5, 0);
            child.Union(a, b);
            child.JournalMark = child.Journal!.Count;
            Assert.True(child.IsClean);
            AssertViolates(child, "canonical ids");
        }

        /// <summary>
        /// An ancestor row hidden with no visible row carrying its information.
        /// </summary>
        [Fact]
        public void AHideWithoutACarrierBreaksHiddenContent()
        {
            var (store, f, a, _) = Setup(freeze: true);
            var child = store.Root.CreateChild();
            var row = store.Schema[f]!.Root.FindRow([a]);
            child.Tables!.GetOrCreateLayer(f).Hide(0, row, HideReason.KeyHeld);
            child.Tables.SealHidden();
            AssertViolates(child, "hidden content");
        }

        /// <summary>
        /// A hidden set left unsealed outside a merge: the read paths probe sealed sets only, so they would miss the
        /// hide.
        /// </summary>
        [Fact]
        public void AnUnsealedHideBreaksSealing()
        {
            var store = new Store(ParallelHeuristics.Serial);
            var f = store.AddTable(MaxFunction(1), name: "f");
            var root = store.Root;
            var a = root.NewId();
            var b = root.NewId();
            Insert(root, f, a, 1, 0);
            Insert(root, f, b, 1, 0);
            root.Freeze();
            var child = root.CreateChild();
            Insert(child, f, a, 5, 0);
            Assert.True(child.Tables!.TryGetLayer(f)!.HiddenOver(0)!.IsSealed);

            // A hide outside a merge, with no seal after it: the sealed set reopens its tail.
            child.Tables.TryGetLayer(f)!.Hide(0, store.Schema[f]!.Root.FindRow([b]), HideReason.KeyHeld);
            Assert.Equal(1, child.Tables.UnsealedCount);
            AssertViolates(child, "sealing");
        }

        /// <summary>
        /// An ancestor appended a row under a key the context holds, and the context's cursor was moved past it without
        /// the catch-up that would fold it in, so two visible rows hold one key.
        /// </summary>
        [Fact]
        public void TwoVisibleRowsWithOneKeyBreakUniqueKeys()
        {
            var (store, f, a, b) = Setup(freeze: false);
            var root = store.Root;
            var child = root.CreateChild();
            Insert(child, f, b, 5, 0);
            Insert(root, f, b, 3, 0);
            Assert.False(child.IsClean);
            SkipCatchUp(child, f);
            Assert.True(child.IsClean);
            AssertViolates(child, "unique keys");
        }

        /// <summary>
        /// A cursor whose stamp moved past an ancestor's change without its row versions.
        /// </summary>
        [Fact]
        public void ACursorBehindItsAncestorBreaksCursors()
        {
            var (store, f, a, _) = Setup(freeze: false);
            var root = store.Root;
            var child = root.CreateChild();
            Insert(root, f, root.NewId(), 3, 0);
            var cursor = child.Tables!.CursorAt(0)!;
            cursor.SetPendingStamp(root.Version);
            cursor.CommitPending();
            Assert.True(child.IsClean);
            AssertViolates(child, "cursors");
        }

        /// <summary>
        /// An ancestor layer compacted under a hidden set (past the gate), so the set names rows of another
        /// generation.
        /// </summary>
        [Fact]
        public void AHiddenSetOfAnotherGenerationBreaksGenerations()
        {
            var (store, f, a, _) = Setup(freeze: false);
            var root = store.Root;
            var child = root.CreateChild();
            Insert(child, f, a, 5, 0);
            Assert.True(child.Tables!.TryGetLayer(f)!.HasHidden);
            store.Schema[f]!.Root.Rehash();
            SkipCatchUp(child, f);
            AssertViolates(child, "generations");
        }

        /// <summary>
        /// An ancestor row names an id local to the context (the tables part of the check that local ids stay local).
        /// </summary>
        [Fact]
        public void AnAncestorRowNamingALocalIdBreaksLocalIds()
        {
            var (store, f, _, _) = Setup(freeze: false);
            var root = store.Root;
            var child = root.CreateChild();
            var local = child.NewId();
            Insert(root, f, local, 2, 0);
            try
            {
                child.Rebuild();
            }
            catch (InvalidOperationException)
            {
                // DEBUG builds check the invariants at the end of the rebuild, which has caught up by then.
            }

            Assert.True(child.IsClean);
            AssertViolates(child, "local ids");
        }

        /// <summary>
        /// An own layer holding staged writes outside a merge (staged without notifying the database).
        /// </summary>
        [Fact]
        public void StagedWritesInALayerBreakAdoption()
        {
            var (store, f, a, _) = Setup(freeze: true);
            var child = store.Root.CreateChild();
            Insert(child, f, a, 5, 0);
            var buffer = child.Tables!.TryGetLayer(f)!.Own.NewBuffer();
            buffer.StageInsert([a, 9, store.ReadClock()]);
            buffer.Flush();
            Assert.True(child.IsClean);
            AssertViolates(child, "adoption");
        }

        /// <summary>
        /// An eq-sort container holds an id the child displaced, and the store's eq-container version was put back, so
        /// the child's guard never saw the container.
        /// </summary>
        [Fact]
        public void AnEqContainerHoldingAnOwnLoserIsReported()
        {
            var store = new Store(ParallelHeuristics.Serial);
            var containers = store.Root.Database.Containers;
            containers.RegisterType<VecContainer>(store.IdCounter, new UnionContainerMerge(store.UnionFindTable, store.ClockCounter));
            var root = store.Root;
            var x = root.NewId();
            var y = root.NewId();
            var child = root.CreateChild();
            child.Union(x, y);
            child.Rebuild();
            child.CheckInvariants();

            var version = containers.EqVersion;
            root.Database.WithExecutionState(state => state.Containers.RegisterValue(new VecContainer(y), state));
            containers.GetEnvironment<VecContainer>().EqVersionCounter!.Value = version;
            Assert.True(child.IsClean);
            AssertViolates(child, "eq-sort containers");
        }

        /// <summary>
        /// A clean, correct context passes, and so does its frozen form.
        /// </summary>
        [Fact]
        public void ACorrectContextPasses()
        {
            var (store, f, a, b) = Setup(freeze: true);
            var child = store.Root.CreateChild();
            Insert(child, f, a, 5, 0);
            Insert(child, f, b, 2, 0);
            child.Union(a, b);
            child.Rebuild();
            child.CheckInvariants();
            child.Freeze();
            child.CheckInvariants();
        }

        /// <summary>
        /// Moves every cursor of <paramref name="child"/> to its ancestors' current state without catching up.
        /// </summary>
        static void SkipCatchUp(Context child, TableId table)
        {
            var tables = child.Tables!;
            for (var a = tables.SettledDepth; a < child.Depth; a++)
            {
                var cursor = tables.CursorAt(a)!;
                cursor.SetPendingRows(table, tables.AncestorLayer(a, table)?.Version ?? default);
                cursor.SetPendingStamp(child.Ancestors[a].Version);
                cursor.CommitPending();
            }
        }

        /// <summary>
        /// An own row whose timestamp lies below the context's creation stamp.
        /// </summary>
        [Fact]
        public void AnOwnRowStampedBeforeTheContextBreaksStamps()
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
            AssertViolates(child, "stamps");
        }

        /// <summary>
        /// The ancestor half of the canonical-ids check: the context displaced an id and was marked rebuilt without the
        /// rebuild step that hides the ancestor rows naming its losers, so the ancestor row naming the loser stays
        /// visible.
        /// </summary>
        [Fact]
        public void AVisibleAncestorRowNamingAnOwnLoserBreaksCanonicalIds()
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
            AssertViolates(child, "canonical ids");
        }

        /// <summary>
        /// A grandchild hides its parent's row while a row with the same key stays visible below it (here the root's,
        /// which the parent never hid because its cursor skipped the catch-up): a hidden row must not let a row of its
        /// key show through from below.
        /// </summary>
        [Fact]
        public void AHiddenRowWhoseKeyIsVisibleBelowIsReported()
        {
            var store = new Store(ParallelHeuristics.Serial);
            var f = store.AddTable(MaxFunction(1), name: "f");
            var root = store.Root;
            var b = root.NewId();
            var child = root.CreateChild();
            Insert(child, f, b, 5, 0);
            Insert(root, f, b, 3, 0);

            // The child skips the catch-up that would fold the root's row into its own, so both rows stay visible.
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
            AssertViolates(grandchild, "keys below a hide");
        }

    }

}

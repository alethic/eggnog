using System;

using Xunit;

using static Eggnog.Relations.Tests.LayeredTestUtil;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// The tables part of the context checker, its container-layers part and its container-log part catch violations:
    /// each test corrupts one invariant deliberately, through internal members no correct operation uses that way, and
    /// asserts that <see cref="Context.CheckInvariants"/> reports it under the checker's label for that invariant.
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
        /// Asserts that the checker throws naming <paramref name="invariant"/>, and returns its message.
        /// </summary>
        static string AssertViolates(Context context, string invariant)
        {
            var e = Assert.Throws<InvalidOperationException>(context.CheckInvariants);
            Assert.Contains(invariant + ":", e.Message);
            return e.Message;
        }

        /// <summary>
        /// Builds a store with the vector type registered at the root, as holding e-class ids, and returns its root
        /// environment.
        /// </summary>
        static (Store Store, ContainerEnv<VecContainer> Env) SetupContainers()
        {
            var store = new Store(ParallelHeuristics.Serial);
            var containers = store.Root.Database.Containers;
            containers.RegisterType<VecContainer>(store.IdCounter, new UnionContainerMerge(store.UnionFindTable, store.ClockCounter));
            return (store, containers.GetEnvironment<VecContainer>());
        }

        /// <summary>
        /// Interns <paramref name="container"/> at the root.
        /// </summary>
        static uint Intern(Context root, VecContainer container) =>
            root.Database.WithExecutionState(state => state.Containers.GetOrInsert(container, state));

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
            child.ContainerLayers!.ContainerMark = child.JournalMark;
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
        /// An eq-sort container the root interned after the child displaced one of its elements holds the child's
        /// loser, and the child's Added cursor into the root's container log was moved past the container's entry
        /// behind the pre-loop scan's back, so the child's catch-up never claimed the container: the child's view holds
        /// a container naming an id that is not canonical in it.
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

            root.Database.WithExecutionState(state => state.Containers.GetOrInsert(new VecContainer(y), state));
            Assert.False(child.IsClean);
            store.ContainerLog.Drain();
            child.Tables!.CursorAt(0)!.Added = store.ContainerLog.AddedCount;
            Assert.True(child.IsClean);
            Assert.Contains($"holds {y}, which is not canonical in the context", AssertViolates(child, "container view"));
        }

        /// <summary>
        /// The child's layer holds a container whose contents are not canonical in the child: its container pass
        /// claimed and rebuilt the root's <c>v = [w]</c> when it displaced <c>w</c>, and a test then placed <c>[w]</c>
        /// under another root id, which no pass rebuilt.
        /// </summary>
        [Fact]
        public void AStaleContainerInALayerBreaksTheContainerView()
        {
            var (store, env) = SetupContainers();
            var root = store.Root;
            var x = root.NewId();
            var w = root.NewId();
            var y = root.NewId();
            Intern(root, new VecContainer(w));
            var u = Intern(root, new VecContainer(y));
            root.Freeze();
            var child = root.CreateChild();
            child.Union(x, w);
            child.Rebuild();
            child.CheckInvariants();

            child.ContainerLayers!.Place(env, new VecContainer(w), u);
            Assert.True(child.IsClean);
            Assert.Contains($"under {u}, whose contents are", AssertViolates(child, "container view"));
        }

        /// <summary>
        /// The child's view holds one content under two ids: a test placed the root's <c>[a]</c>, which the root holds
        /// under <c>v1</c>, in the child's layer under the root's <c>v2</c>.
        /// </summary>
        [Fact]
        public void AContentSeenUnderTwoIdsBreaksTheContainerView()
        {
            var (store, env) = SetupContainers();
            var root = store.Root;
            var a = root.NewId();
            var b = root.NewId();
            var v1 = Intern(root, new VecContainer(a));
            var v2 = Intern(root, new VecContainer(b));
            root.Freeze();
            var child = root.CreateChild();
            child.ContainerLayers!.Place(env, new VecContainer(a), v2);
            Assert.True(child.IsClean);
            Assert.Contains($"sees it at a level below under {v1} too", AssertViolates(child, "container view"));
        }

        /// <summary>
        /// The child sees a root container under an id it displaced: it unioned the ids of two root containers and was
        /// marked rebuilt without its container pass, which would have claimed the loser's container.
        /// </summary>
        [Fact]
        public void ARootContainerUnderAnOwnLoserBreaksTheContainerView()
        {
            var (store, _) = SetupContainers();
            var root = store.Root;
            var a = root.NewId();
            var b = root.NewId();
            var v1 = Intern(root, new VecContainer(a));
            var v2 = Intern(root, new VecContainer(b));
            root.Freeze();
            var child = root.CreateChild();
            child.Union(v1, v2);
            child.JournalMark = child.Journal!.Count;
            child.ContainerLayers!.ContainerMark = child.JournalMark;
            Assert.True(child.IsClean);
            Assert.Contains($"under {v2}, which is not canonical in the context", AssertViolates(child, "container view"));
        }

        /// <summary>
        /// The child's container pass has not consumed its journal: the container mark lies behind the journal's rows,
        /// which a rebuild cut short leaves, so the child is not clean.
        /// </summary>
        [Fact]
        public void AContainerMarkBehindTheJournalLeavesTheChildUnclean()
        {
            var (store, _) = SetupContainers();
            var root = store.Root;
            var a = root.NewId();
            var b = root.NewId();
            var child = root.CreateChild();
            child.Union(a, b);
            child.Rebuild();
            Assert.True(child.IsClean);
            child.ContainerLayers!.ContainerMark = 0;
            Assert.False(child.IsClean);
            Assert.False(child.IsViewCurrent);
            child.Rebuild();
            Assert.Equal(child.Journal!.Count, child.ContainerLayers.ContainerMark);
            Assert.True(child.IsClean);
        }

        /// <summary>
        /// A cursor whose Added position lies past the end of its ancestor's container log: only a clear could leave one
        /// there, and the log is not cleared while the context lives.
        /// </summary>
        [Fact]
        public void ACursorPastItsAncestorsContainerLogBreaksCursors()
        {
            var (store, _, _, _) = Setup(freeze: false);
            var child = store.Root.CreateChild();
            child.CheckInvariants();
            child.Tables!.CursorAt(0)!.Added = store.ContainerLog.AddedCount + 1;
            Assert.True(child.IsClean);
            Assert.Contains("the cursor into the container log at depth 0", AssertViolates(child, "cursors"));
        }

        /// <summary>
        /// A container of classes recorded in a frozen level's container log, through the library's root intern, which
        /// trusts its caller to keep the root writable: a frozen level's log never grows, neither in its buffers nor,
        /// once drained, in its Added list.
        /// </summary>
        [Fact]
        public void AFrozenLevelsLogThatGrowsBreaksTheContainerLog()
        {
            var (store, env) = SetupContainers();
            var root = store.Root;
            var w = root.NewId();
            var child = root.CreateChild();
            root.Freeze();
            Assert.True(store.ContainerLog.Active);
            root.CheckInvariants();

            env.GetOrInsertAtRoot(new VecContainer(w, w), store.Counters);
            Assert.True(store.ContainerLog.Pending);
            Assert.True(root.IsClean);
            Assert.Contains("when its level froze", AssertViolates(root, "container log"));
            store.ContainerLog.Drain();
            Assert.Equal(1, store.ContainerLog.AddedCount);
            Assert.Contains("when its level froze", AssertViolates(root, "container log"));
            child.Dispose();
        }

        /// <summary>
        /// A root environment that buffered an entry for the root's container log is attached to a child's log behind
        /// the root log's back: the root's log drains an environment that records into another log, and the child's,
        /// inactive since the child has no descendant, holds the entry.
        /// </summary>
        [Fact]
        public void AnEnvironmentMovedToAnotherLogBreaksTheContainerLog()
        {
            var (store, env) = SetupContainers();
            var root = store.Root;
            var w = root.NewId();
            var child = root.CreateChild();
            root.Database.WithExecutionState(state => state.Containers.GetOrInsert(new VecContainer(w), state));
            Assert.True(store.ContainerLog.Pending);
            root.CheckInvariants();
            child.CheckInvariants();

            child.ContainerLog.Attach(env);
            Assert.False(child.ContainerLog.Active);
            Assert.True(child.IsClean);
            Assert.Contains("records into another log", AssertViolates(root, "container log"));
            Assert.Contains("is inactive but holds", AssertViolates(child, "container log"));
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
            child.ContainerLayers!.ContainerMark = child.JournalMark;
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

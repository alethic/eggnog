using System;
using System.Linq;

using Xunit;

using static Eggnog.Relations.Tests.LayeredTestUtil;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// Tests for the catch-up of ancestor rows, one per branch: (a) a row naming an id not canonical in the child, (b)
    /// a row whose key the child holds, (c′) a row stamped below the child's semi-naive floor, (c) a row visible as it
    /// is; plus the contradiction trigger, the generation check, the cursors, an ancestor change that only tombstones,
    /// a parent that wrote and froze, and the refusal to catch up with a parent a contradiction stopped.
    /// </summary>
    public class CatchUpTests
    {

        readonly ITestOutputHelper output;

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="output">The test output.</param>
        public CatchUpTests(ITestOutputHelper output)
        {
            this.output = output;
        }

        /// <summary>
        /// (a): an ancestor appends a row naming an id the child displaced; the child copies it canonically and hides
        /// it.
        /// </summary>
        [Fact]
        public void AnAppendNamingAChildLoserIsCopiedCanonically()
        {
            var store = new Store(ParallelHeuristics.Serial);
            var f = store.AddTable(MaxFunction(1), name: "f");
            var root = store.Root;
            var a = root.NewId();
            var b = root.NewId();
            var child = root.CreateChild();
            child.Union(a, b);
            child.Rebuild();

            Insert(root, f, b, 4, 0);
            Assert.False(child.IsClean);
            child.Rebuild();

            var statistics = child.Tables!.Statistics;
            Assert.Equal(1, statistics.CatchUpRowsExamined);
            Assert.Equal(1, statistics.RowsCopied);
            Assert.Equal(4u, Row(child, f, a)![1]);
            Assert.Null(Row(child, f, b));
            var layer = child.Tables.TryGetLayer(f)!;
            Assert.Equal(HideReason.Mentions, layer.ReasonFor(0, 0) ?? HideReason.Mentions);
            Assert.True(layer.IsHiddenHere(0, 0));
            Assert.Equal(4u, Row(root, f, b)![1]);
            child.CheckInvariants();
        }

        /// <summary>
        /// (b): an ancestor appends a row whose key the child holds; the child folds it into its own row and hides it.
        /// </summary>
        [Fact]
        public void AnAppendUnderAKeyTheChildHoldsIsFoldedIn()
        {
            var store = new Store(ParallelHeuristics.Serial);
            var f = store.AddTable(MaxFunction(1), name: "f");
            var root = store.Root;
            var a = root.NewId();
            var c = root.NewId();
            var child = root.CreateChild();
            Insert(child, f, a, 5, 0);
            Insert(child, f, c, 2, 0);

            Insert(root, f, a, 3, 0);
            Insert(root, f, c, 8, 0);
            child.Rebuild();

            // The smaller root value adds nothing to the child's f(a) = 5; the larger one raises its f(c) to 8.
            Assert.Equal(5u, Row(child, f, a)![1]);
            Assert.Equal(8u, Row(child, f, c)![1]);
            var statistics = child.Tables!.Statistics;
            Assert.Equal(2, statistics.CatchUpRowsExamined);
            Assert.Equal(2, statistics.RowsCopied);
            Assert.Equal(2, child.GetView(f).VisibleCount);
            Assert.Equal(3u, Row(root, f, a)![1]);
            child.CheckInvariants();
            LookupPaths.Check(child);
        }

        /// <summary>
        /// (c): an append the child neither names a loser of nor holds the key of is visible as it is, without a copy.
        /// </summary>
        [Fact]
        public void AnUntouchedAppendIsVisibleAsItIs()
        {
            var store = new Store(ParallelHeuristics.Serial);
            var f = store.AddTable(MaxFunction(1), name: "f");
            var root = store.Root;
            var a = root.NewId();
            var b = root.NewId();
            var child = root.CreateChild();
            Insert(child, f, a, 1, 0);
            Insert(root, f, b, 7, 0);
            child.Rebuild();

            Assert.Equal(1, child.Tables!.Statistics.CatchUpRowsExamined);
            Assert.Equal(0, child.Tables.Statistics.RowsCopied);
            Assert.True(child.GetView(f).FindRow([b], out var layer) >= 0);
            Assert.Equal(0, layer);
            Assert.Equal(7u, Row(child, f, b)![1]);
        }

        /// <summary>
        /// (c′): a raw ancestor append stamped below the child's semi-naive floor (raised by a rule run noted at a
        /// later epoch) is re-stamped by a copy; one stamped at or above the floor is not.
        /// </summary>
        [Fact]
        public void AnAppendBelowTheSemiNaiveFloorIsRestamped()
        {
            var store = new Store(ParallelHeuristics.Serial);
            var f = store.AddTable(MaxFunction(1), name: "f");
            var root = store.Root;
            var a = root.NewId();
            var b = root.NewId();
            var child = root.CreateChild();
            var created = child.CreatedAt;
            child.NoteRuleRun(created + 5);
            Assert.Equal(created + 5, child.SemiNaiveFloor);

            // Raw root writes through the database, which stamps what it is given (the root's raw-write contract).
            for (var i = 0; i < 6; i++)
                store.AdvanceClock();

            root.Database.WithExecutionState(state =>
            {
                state.StageInsert(f, [a, 1, created + 1]);
                return 0;
            });
            root.Database.MergeAll(created + 1);
            root.Database.WithExecutionState(state =>
            {
                state.StageInsert(f, [b, 2, created + 5]);
                return 0;
            });
            root.Database.MergeAll(created + 5);

            child.Rebuild();
            var statistics = child.Tables!.Statistics;
            Assert.Equal(1, statistics.RestampedCopies);
            Assert.Equal(1, statistics.RowsCopied);
            var restamped = Row(child, f, a)!;
            Assert.True(restamped[2] >= created + 5, "the copy carries the catch-up's epoch");
            Assert.True(child.GetView(f).FindRow([b], out var layer) >= 0);
            Assert.Equal(0, layer);
            Assert.Equal(created + 5, Row(child, f, b)![2]);
            child.CheckInvariants();
        }

        /// <summary>
        /// A case whose ancestor asserts the contradiction catches it up and stops; it then answers only
        /// <see cref="Context.IsContradictory"/>.
        /// </summary>
        [Fact]
        public void ACaseStopsWhenItCatchesUpAContradiction()
        {
            var store = new Store(ParallelHeuristics.Serial);
            var bottom = store.AddTable(Relation(0), name: "bottom");
            store.DesignateContradiction(bottom);
            var root = store.Root;
            var a = root.NewId();
            var plain = root.CreateChild();
            var caseChild = root.CreateChild(ContextOptions.Case);
            Insert(root, bottom, 0);

            caseChild.Rebuild();
            Assert.True(caseChild.IsContradicted);
            Assert.True(caseChild.IsContradictory);
            Assert.Throws<InvalidOperationException>(() => caseChild.Find(a));

            // Without the option, the contradiction is an ordinary fact.
            plain.Rebuild();
            Assert.False(plain.IsContradicted);
            Assert.True(plain.IsContradictory);
            Assert.Equal(a, plain.Find(a));
        }

        /// <summary>
        /// An ancestor layer that changes generation under a live descendant (here by a compaction forced past the
        /// gate) renumbers the rows the descendant's cursors and hidden sets name; the descendant's catch-up refuses,
        /// which faults it.
        /// </summary>
        [Fact]
        public void AGenerationChangeUnderADescendantFaultsIt()
        {
            var store = new Store(ParallelHeuristics.Serial);
            var f = store.AddTable(MaxFunction(1), name: "f");
            var root = store.Root;
            var ids = Enumerable.Range(0, 4).Select(_ => root.NewId()).ToArray();
            Insert(root, f, ids[0], 1, 0);
            var child = root.CreateChild();
            Insert(child, f, ids[1], 1, 0);

            // Bypass the gate: what the gate prevents.
            Insert(root, f, ids[0], 2, 0);
            store.Schema[f]!.Root.Rehash();
            Assert.Throws<InvalidOperationException>(() => child.Rebuild());
            Assert.True(child.IsFaulted);
            child.Dispose();
        }

        /// <summary>
        /// Cursors: after a rebuild every cursor is at its ancestor's versions and stamp, and frozen ancestors read in
        /// full are settled and drop their cursors.
        /// </summary>
        [Fact]
        public void CursorsCommitAndSettle()
        {
            var store = new Store(ParallelHeuristics.Serial);
            var f = store.AddTable(MaxFunction(1), name: "f");
            var root = store.Root;
            var a = root.NewId();
            var child = root.CreateChild();
            var grandchild = child.CreateChild();
            Assert.Equal(0, grandchild.Tables!.SettledDepth);

            Insert(root, f, a, 1, 0);
            Assert.True(grandchild.Tables.NeedsCatchUp);
            grandchild.Rebuild();
            Assert.False(grandchild.Tables.NeedsCatchUp);
            Assert.Equal(store.Schema[f]!.Root.Version, grandchild.Tables.CursorAt(0)!.RowsOrDefault(f));
            Assert.Equal(root.Version, grandchild.Tables.CursorAt(0)!.Stamp);

            // Freezing changes nothing the grandchild reads, so it stays clean; its next rebuild that runs a pass
            // settles the frozen ancestors.
            root.Freeze();
            child.Freeze();
            Assert.True(grandchild.IsClean);
            Assert.Equal(1u, Row(grandchild, f, a)![1]);
            var b = grandchild.NewId();
            Insert(grandchild, f, a, 3, 0);
            grandchild.Union(a, b);
            grandchild.Rebuild();
            Assert.Equal(2, grandchild.Tables.SettledDepth);
            Assert.Null(grandchild.Tables.CursorAt(0));
            Assert.Equal(3u, Row(grandchild, f, a)![1]);
            grandchild.CheckInvariants();
        }

        /// <summary>
        /// A row a grandparent appends reaches the grandchild through its own cursor into the grandparent, while the
        /// parent in between hides an older row of the same key.
        /// </summary>
        [Fact]
        public void AGrandchildCatchesUpWithEveryUnsettledAncestor()
        {
            var store = new Store(ParallelHeuristics.Serial);
            var f = store.AddTable(MaxFunction(1), name: "f");
            var root = store.Root;
            var ids = Enumerable.Range(0, 3).Select(_ => root.NewId()).ToArray();
            Insert(root, f, ids[0], 1, 0);
            var parent = root.CreateChild();
            Insert(parent, f, ids[0], 4, 0);
            var child = parent.CreateChild();
            Insert(child, f, ids[1], 1, 0);

            Insert(root, f, ids[2], 9, 0);
            Insert(root, f, ids[0], 6, 0);
            child.Rebuild();

            // The parent caught up first (its f(0) = 6 now); the child sees the parent's row and the root's new f(2).
            Assert.Equal(6u, Row(parent, f, ids[0])![1]);
            Assert.Equal(6u, Row(child, f, ids[0])![1]);
            Assert.Equal(9u, Row(child, f, ids[2])![1]);
            Assert.Equal(1u, Row(child, f, ids[1])![1]);
            child.CheckInvariants();
            parent.CheckInvariants();
            LookupPaths.Check(child);
        }

        /// <summary>
        /// A table added to the root after a child exists is caught up like any other: the child's cursor has not seen
        /// its layer, so every row the root writes into it counts as new; a grandchild under a writable parent too.
        /// </summary>
        [Fact]
        public void ATableAddedAfterTheChildIsCaughtUp()
        {
            var store = new Store(ParallelHeuristics.Serial);
            store.AddTable(MaxFunction(1), name: "f");
            var root = store.Root;
            var a = root.NewId();
            var b = root.NewId();
            var child = root.CreateChild();
            var grandchild = child.CreateChild();
            child.Union(a, b);

            var g = store.AddTable(MaxFunction(1), name: "g");
            Insert(root, g, b, 3, 0);
            Insert(root, g, a, 1, 0);
            grandchild.Rebuild();

            // In the child (and its grandchild) a = b, so g(a) holds max(1, 3).
            Assert.Equal(3u, Row(child, g, a)![1]);
            Assert.Equal(3u, Row(grandchild, g, a)![1]);
            Assert.Null(Row(grandchild, g, b));
            Assert.Equal(1u, Row(root, g, a)![1]);
            child.CheckInvariants();
            grandchild.CheckInvariants();
            LookupPaths.Check(grandchild, [[a], [b]]);
        }

        /// <summary>
        /// The parent writes a row while its child lives and then freezes, which is allowed: the child catches the row
        /// up once, through a direct rebuild or through its own freeze, and neither faults it.
        /// </summary>
        [Fact]
        public void AChildCatchesUpWithAParentThatWroteAndFroze()
        {
            var store = new Store(ParallelHeuristics.Serial);
            var f = store.AddTable(MaxFunction(1), name: "f");
            var root = store.Root;
            var a = root.NewId();
            root.Freeze();
            var parent = root.CreateChild();
            var child = parent.CreateChild();

            // The parent writes while its child lives, then freezes (allowed: the child catches up once).
            Insert(parent, f, a, 1, 0);
            parent.Freeze();

            // The row is visible as it is (catch-up branch c): nothing is copied, so the rebuild changes nothing of the
            // child's.
            Assert.False(child.Rebuild());
            Assert.False(child.IsFaulted);
            Assert.True(child.IsClean);
            Assert.Equal(1u, Row(child, f, a)![1]);

            child.Freeze();
            Assert.False(child.IsFaulted);
            Assert.True(child.IsFrozen);
            Assert.Equal(1u, Row(child, f, a)![1]);
            Assert.Equal(0, child.Tables!.Statistics.RowsCopied);
        }

        /// <summary>
        /// A child of a case that a contradiction stopped: the case answers only <c>IsContradicted</c> and stays as its
        /// merge left it (writes pending). A child catches up only with a parent that is frozen or clean, so its
        /// rebuild is refused cleanly with <see cref="InvalidOperationException"/>, before any pass: it neither trips
        /// the debug assertion of <c>CatchUpUnionFind</c> (a process crash without a listener) nor runs passes it can
        /// never become clean through.
        /// </summary>
        [Fact]
        public void AChildOfAStoppedCaseIsNotCaughtUpWithItsUncleanParent()
        {
            var store = new Store(ParallelHeuristics.Serial);
            var bottom = store.AddTable(Relation(0), name: "bottom");
            store.DesignateContradiction(bottom);
            var g = store.AddTable(Constructor(store), [], [store.UnionFindTable], "g");
            var root = store.Root;
            var a = root.NewId();
            var x = root.NewId();
            var y = root.NewId();
            Insert(root, g, a, x, 0);
            root.Freeze();

            var @case = root.CreateChild(ContextOptions.Case);
            var child = @case.CreateChild();

            // The case stops with the union x = y staged and unmerged.
            @case.Write(state =>
            {
                state.StageInsert(bottom, [state.Timestamp]);
                state.StageInsert(g, [a, y, state.Timestamp]);
                return 0;
            });
            Assert.True(@case.IsContradicted);
            Assert.False(@case.IsClean);

            using var scope = new DebugAssertScope();
            Exception? thrown = null;
            try
            {
                child.Rebuild();
            }
            catch (Exception e)
            {
                thrown = e;
            }

            output.WriteLine($"child.Rebuild() threw {thrown?.GetType().Name}: {thrown?.Message}");
            Assert.False(thrown is DebugAssertFailedException, $"the child caught up with a stopped, unclean parent: {thrown?.Message}");
            Assert.True(thrown is InvalidOperationException, "the child of a stopped case should be refused");
        }

        /// <summary>
        /// The same with no assertion to trip, as in a release build: the child of the stopped case is refused cleanly
        /// and not faulted (or is clean), rather than running passes on every <c>Rebuild</c> (each taking an epoch)
        /// that never make it clean, while accepting writes that merge against the half-merged parent.
        /// </summary>
        [Fact]
        public void AChildOfAStoppedCaseIsNeitherRefusedNorEverClean()
        {
            var store = new Store(ParallelHeuristics.Serial);
            var bottom = store.AddTable(Relation(0), name: "bottom");
            store.DesignateContradiction(bottom);
            var g = store.AddTable(Constructor(store), [], [store.UnionFindTable], "g");
            var root = store.Root;
            var a = root.NewId();
            var x = root.NewId();
            var y = root.NewId();
            Insert(root, g, a, x, 0);
            root.Freeze();

            var @case = root.CreateChild(ContextOptions.Case);
            var child = @case.CreateChild();
            @case.Write(state =>
            {
                state.StageInsert(bottom, [state.Timestamp]);
                state.StageInsert(g, [a, y, state.Timestamp]);
                return 0;
            });

            using var scope = new DebugAssertScope();
            var clock = store.ReadClock();
            Exception? first = null;
            try
            {
                child.Rebuild();
                child.Rebuild();
            }
            catch (Exception e)
            {
                first = e;
            }

            output.WriteLine($"threw: {first?.GetType().Name}: {first?.Message}; clean: {child.IsClean}; faulted: {child.IsFaulted}; clock {clock} -> {store.ReadClock()}");
            var refusedCleanly = first is InvalidOperationException && first is not DebugAssertFailedException && child.IsFaulted == false;
            Assert.True(refusedCleanly || child.IsClean, "the child of a stopped case is neither refused nor ever clean");
        }

    }

}

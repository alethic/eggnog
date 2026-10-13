using System;

using Xunit;

using static Eggnog.Relations.Tests.LayeredTestUtil;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// Adversarial soundness checks of the M9.4 implementation (child rebuild, catch-up, settle, freeze, dispose, the
    /// container interim). Each test states the behaviour the spec (or its principles) requires; a failing test is a
    /// defect.
    /// </summary>
    public class M94SoundnessReviewTests
    {

        readonly ITestOutputHelper output;

        public M94SoundnessReviewTests(ITestOutputHelper output)
        {
            this.output = output;
        }

        static (Store Store, TableId F) ContainerSetup()
        {
            var store = new Store(ParallelHeuristics.Serial);
            var f = store.AddTable(MaxFunction(1), name: "f");
            store.Root.Database.Containers.RegisterType<VecContainer>(store.IdCounter, new UnionContainerMerge(store.UnionFindTable, store.ClockCounter), holdsIds: true);
            return (store, f);
        }

        static uint Intern(Context context, VecContainer container) =>
            context.Database.WithExecutionState(state => state.Containers.RegisterValue(container, state));

        /// <summary>
        /// G-b (§7.4, decision 9): a child may not hold a view in which an eq-sort container names one of its own losers,
        /// since children never rebuild containers. The guard checks each journal row once, when the pass that appended it
        /// ends; a container the (writable) root interns afterwards, naming the child's loser, is never checked, so the
        /// child keeps a view that is not a copy: in a fresh root replaying the chain, the containers [x] and [y] become
        /// equal once x and y are, and their ids are unioned.
        /// </summary>
        [Fact]
        public void AnEqContainerInternedByTheRootAfterAChildDisplacedItsElementIsRefused()
        {
            var (store, f) = ContainerSetup();
            var root = store.Root;
            var x = root.NewId();
            var y = root.NewId();
            var child = root.CreateChild();

            // y loses to x in the child; no container holds y yet, so the guard lets it pass.
            child.Union(x, y);
            child.Rebuild();
            Assert.Equal(x, child.Find(y));

            // The root (not frozen) interns [x] and [y] and names them in rows the child will read.
            var kx = Intern(root, new VecContainer(x));
            var ky = Intern(root, new VecContainer(y));
            Insert(root, f, kx, 1, 0);
            Insert(root, f, ky, 2, 0);

            // Sound: the child is refused (NotSupportedException, faulted), or the two containers are equal in it.
            Exception? refused = null;
            try
            {
                child.Rebuild();
            }
            catch (NotSupportedException e)
            {
                refused = e;
            }

            if (refused is null)
                Assert.True(child.AreEqual(kx, ky), $"the child equates {x} and {y} but keeps the containers [{x}] (id {kx}) and [{y}] (id {ky}) apart, and was not refused (G-b)");
        }

        /// <summary>
        /// The copy property for G-b: the same chain replayed in a fresh root unions the two container ids.
        /// </summary>
        [Fact]
        public void TheFreshRootCopyOfThatChainEquatesTheContainers()
        {
            var (store, f) = ContainerSetup();
            var root = store.Root;
            var x = root.NewId();
            var y = root.NewId();
            var kx = Intern(root, new VecContainer(x));
            var ky = Intern(root, new VecContainer(y));
            Insert(root, f, kx, 1, 0);
            Insert(root, f, ky, 2, 0);
            root.Union(x, y);
            root.Rebuild();
            Assert.True(root.AreEqual(kx, ky));
        }

        /// <summary>
        /// A child of a case that a contradiction stopped (§6.9): the case answers only IsContradictory and stays as its
        /// merge left it (writes pending). Its child must not catch up with that half-merged, unclean parent (the
        /// precondition of §5.6 and §7.1: "require parent frozen or clean"); it should be refused cleanly. Here the
        /// child's rebuild runs passes against the unclean parent and trips the debug assertion of
        /// <c>CatchUpUnionFind</c> (a process crash without a listener), and the child can never become clean.
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
        /// The release-build consequence of the defect above: with no assertion, the child of the stopped case runs
        /// passes on every <c>Rebuild</c> (each takes an epoch), never becomes clean (its parent never will), and still
        /// accepts writes that merge against the half-merged parent.
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

        /// <summary>
        /// §13 edge 22 (the implementer's storage-level refusal): a failure recorded and not taken must not be frozen into
        /// a context that descendants share. The check runs before Freeze's own rebuild, so a failure that rebuild records
        /// (a merge conflict found by step 4) is frozen in.
        /// </summary>
        [Fact]
        public void FreezeDoesNotFreezeAFailureItsOwnRebuildRecorded()
        {
            var store = new Store(ParallelHeuristics.Serial);
            var g = store.AddTable(new SortedWritesTable(1, 3, C(2), [C(0)], new AssertEqualMerge(C(1), "g"), ParallelHeuristics.Serial), name: "g");
            var root = store.Root;
            var a = root.NewId();
            var b = root.NewId();
            Insert(root, g, a, 1, 0);
            Insert(root, g, b, 2, 0);
            root.Freeze();

            var child = root.CreateChild();
            child.Union(a, b);
            Assert.Null(child.Database.Failure);

            Exception? thrown = null;
            try
            {
                child.Freeze();
            }
            catch (InvalidOperationException e)
            {
                thrown = e;
            }

            output.WriteLine($"frozen: {child.IsFrozen}, failure: {child.Database.Failure ?? "<none>"}, threw: {thrown?.Message}");
            Assert.False(child.IsFrozen && child.Database.Failure is not null, $"the child froze with the failure '{child.Database.Failure}' that its freeze's rebuild recorded");
        }

    }

}

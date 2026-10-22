using System;

using Xunit;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// Tests for <see cref="PairContainer"/>, egglog's pair container, and for interning containers that hold no e-class
    /// ids without an execution state (<see cref="ContainerEnv{TContainer}.GetOrInsertShared"/>).
    /// </summary>
    public class PairContainerTests
    {

        static ContainerEnv<PairContainer> NewEnv(CounterId counter, bool holdsIds)
        {
            return new ContainerEnv<PairContainer>(
                new ContainerTypeId(0), NoMergeContainerMerge.Instance, counter, ParallelHeuristics.Serial, holdsIds);
        }

        [Fact]
        public void TheRebuildFlagsArePartOfAPairsIdentity()
        {
            // egglog derives Eq and Hash over all four fields (src/sort/pair.rs:3-9).
            var values = new PairContainer(false, false, 1, 2);
            var eq = new PairContainer(true, false, 1, 2);
            Assert.NotEqual(values, eq);
            Assert.NotEqual(values.ComputeHash(), eq.ComputeHash());

            var db = new Database();
            var env = NewEnv(db.AddCounter(), holdsIds: false);
            Assert.NotEqual(env.GetOrInsertShared(values, db.Counters), env.GetOrInsertShared(eq, db.Counters));
        }

        [Fact]
        public void InterningWithoutAStateGivesTheStatesId()
        {
            var db = new Database();
            var counter = db.AddCounter();
            var env = NewEnv(counter, holdsIds: false);
            var shared = env.GetOrInsertShared(new PairContainer(false, false, 3, 4), db.Counters);
            var viaState = db.WithExecutionState(state => env.GetOrInsert(new PairContainer(false, false, 3, 4), state));
            Assert.Equal(shared, viaState);
            Assert.Equal(1, db.ReadCounter(counter));
            Assert.True(env.TryGetContainer(shared, out var pair));
            Assert.Equal(new PairContainer(false, false, 3, 4), pair);
            env.CheckInvariants();
        }

        [Fact]
        public void ContainersHoldingIdsNeedAState()
        {
            var db = new Database();
            var env = NewEnv(db.AddCounter(), holdsIds: true);
            Assert.Throws<InvalidOperationException>(() => env.GetOrInsertShared(new PairContainer(true, true, 1, 2), db.Counters));
        }

        [Fact]
        public void OnlyFlaggedElementsAreRebuilt()
        {
            // egglog's rebuild_contents (pair.rs:12-24): a flagged element is canonicalized, an unflagged one kept.
            var rebuilder = new FakeRebuilder(oldOuterId: 1, newOuterId: 9, oldInnerValue: null, newInnerValue: null);
            Assert.True(new PairContainer(true, false, 1, 1).TryRebuild(rebuilder, out var first));
            Assert.Equal(new PairContainer(true, false, 9, 1), first);
            Assert.True(new PairContainer(false, true, 1, 1).TryRebuild(rebuilder, out var second));
            Assert.Equal(new PairContainer(false, true, 1, 9), second);
            Assert.False(new PairContainer(false, false, 1, 1).TryRebuild(rebuilder, out _));
            Assert.False(new PairContainer(true, true, 2, 3).TryRebuild(rebuilder, out _));
        }

        [Fact]
        public void PairsOfValuesAndOfClassesShareAnEnvironment()
        {
            // egglog keeps a container type's eq-sort and base containers in one environment, apart by do_rebuild.
            var egraph = BridgeTestUtil.NewEGraph();
            var held = egraph.RegisterEqContainerType<PairContainer>();
            Assert.True(held.HoldsIds);
            Assert.Same(held, egraph.RegisterBaseContainerType<PairContainer>());

            var other = BridgeTestUtil.NewEGraph();
            var env = other.RegisterBaseContainerType<PairContainer>();
            Assert.False(env.HoldsIds);
            Assert.Same(env, other.RegisterBaseContainerType<PairContainer>());
            Assert.Same(env, other.RegisterEqContainerType<PairContainer>());
            Assert.True(env.HoldsIds);

            // A pair of values still interns without an execution state; one of classes only at the root's.
            var values = env.GetOrInsertShared(new PairContainer(false, false, 1, 2), other.Counters);
            Assert.Equal(values, env.GetOrInsertShared(new PairContainer(false, false, 1, 2), other.Counters));
            Assert.Throws<InvalidOperationException>(() => env.GetOrInsertShared(new PairContainer(true, false, 1, 2), other.Counters));
            Assert.NotEqual(values, env.GetOrInsertAtRoot(new PairContainer(true, false, 1, 2), other.Counters));
        }

    }

}

using System;
using System.Threading;

using Xunit;

using static Eggnog.Relations.Tests.BridgeTestUtil;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// Every path that mints an id applies one bound, <see cref="UnionFind.MaxId"/>, and throws
    /// <see cref="OverflowException"/> past it: the root's own mints, a child's blocks, a fresh id or term at the root
    /// through the bridge, and a container's id. Running out of ids in a child's rule search, or of the clock before a
    /// rebuild pass or a freeze, leaves the child unfaulted. <see cref="Store.IdsReserved"/> and <see cref="Store.EpochsUsed"/>
    /// report the counters, capped at their limits.
    /// </summary>
    public sealed class StoreExhaustionTests
    {

        /// <summary>
        /// The id limit is every id a union-find can hold, the epoch limit every clock value below the stale marker, and
        /// a fresh store reports one of each once it has handed one out.
        /// </summary>
        [Fact]
        public void TheLimitsAreTheUnionFindsIdsAndTheEpochsBelowTheStaleMarker()
        {
            Assert.Equal(UnionFind.MaxId + 1L, Store.IdLimit);
            Assert.Equal(Value.StaleRep - 1L, Store.EpochLimit);

            var store = new Store();
            Assert.Equal(0, store.IdsReserved);
            Assert.Equal(0, store.EpochsUsed);
            Assert.False(store.IdsExhausted);
            store.Root.NewId();
            store.Root.EndEpoch();
            Assert.Equal(1, store.IdsReserved);
            Assert.Equal(1, store.EpochsUsed);
        }

        /// <summary>
        /// The counters may pass their limits (a refused block still moves the id counter); the reports stop at them.
        /// </summary>
        [Fact]
        public void TheReportsStopAtTheLimits()
        {
            var store = new Store();
            store.Counters.IncrementBy(store.IdCounter, Store.IdLimit + 100);
            store.Counters.IncrementBy(store.ClockCounter, Store.EpochLimit + 100);

            Assert.Equal(Store.IdLimit, store.IdsReserved);
            Assert.Equal(Store.EpochLimit, store.EpochsUsed);
            Assert.True(store.IdsExhausted);
        }

        /// <summary>
        /// The store has run out of ids exactly once the last id is handed out.
        /// </summary>
        [Fact]
        public void TheIdsAreExhaustedOnceTheLastIdIsHandedOut()
        {
            var store = new Store();
            store.Counters.IncrementBy(store.IdCounter, UnionFind.MaxId);
            Assert.False(store.IdsExhausted);
            Assert.Equal(UnionFind.MaxId, store.Root.NewId());
            Assert.True(store.IdsExhausted);
            Assert.Equal(Store.IdLimit, store.IdsReserved);
        }

        /// <summary>
        /// <see cref="Store.CheckId"/> admits every id up to <see cref="UnionFind.MaxId"/> and refuses the next.
        /// </summary>
        [Fact]
        public void CheckIdAdmitsTheLastIdAndRefusesTheNext()
        {
            Assert.Equal(UnionFind.MaxId, Store.CheckId(UnionFind.MaxId));
            Assert.Equal(0u, Store.CheckId(0));
            Assert.Throws<OverflowException>(() => Store.CheckId(UnionFind.MaxId + 1L));
            Assert.Throws<OverflowException>(() => Store.CheckId(Value.StaleRep));
        }

        /// <summary>
        /// The bridge's fresh id at the root hands out the last id and refuses the next with
        /// <see cref="OverflowException"/>, below <see cref="Value.StaleRep"/>, where a 32-bit conversion alone would
        /// still pass.
        /// </summary>
        [Fact]
        public void TheBridgesFreshIdStopsAtTheLastId()
        {
            var g = new ContextGraph();
            var store = g.EGraph.Store;
            store.Counters.IncrementBy(store.IdCounter, UnionFind.MaxId - store.Counters.Read(store.IdCounter));

            Assert.Equal(UnionFind.MaxId, g.EGraph.FreshId().Rep);
            Assert.Throws<OverflowException>(() => g.EGraph.FreshId());
            Assert.Throws<OverflowException>(() => g.EGraph.FreshId());
            Assert.True(store.IdsExhausted);
        }

        /// <summary>
        /// A term added at the root as egglog adds it takes the root's fresh id, and is refused past the last one.
        /// </summary>
        [Fact]
        public void AddTermAtTheRootStopsAtTheLastId()
        {
            var g = new ContextGraph();
            var store = g.EGraph.Store;
            store.Counters.IncrementBy(store.IdCounter, Store.IdLimit - store.Counters.Read(store.IdCounter));

            Assert.Throws<OverflowException>(() => g.EGraph.AddTerm(g.Num, [g.Int(1)]));
            Assert.False(g.Root.IsFaulted);
        }

        /// <summary>
        /// A container's id comes from the same counter under the same bound: past the last id, interning a new
        /// container throws <see cref="OverflowException"/>, and one already interned is still found.
        /// </summary>
        [Fact]
        public void AContainerIdStopsAtTheLastId()
        {
            var g = new ContextGraph();
            g.EGraph.RegisterContainerType<SetContainer>(holdsIds: false);
            var known = g.EGraph.GetContainerValue(new SetContainer(1, 2));
            var store = g.EGraph.Store;
            store.Counters.IncrementBy(store.IdCounter, Store.IdLimit - store.Counters.Read(store.IdCounter));

            Assert.Throws<OverflowException>(() => g.EGraph.GetContainerValue(new SetContainer(3, 4)));
            Assert.Equal(known, g.EGraph.GetContainerValue(new SetContainer(2, 1)));
        }

        /// <summary>
        /// An <see cref="OverflowException"/> that a child's rule search throws while the store still has ids is not
        /// exhaustion: it faults the child, as any other exception escaping its run does.
        /// </summary>
        [Fact]
        public void AnOverflowThatIsNotExhaustionStillFaultsTheChild()
        {
            var g = new ContextGraph();
            var overflow = Register(g.EGraph, (_, _) => throw new OverflowException("not the store's"));
            var rb = g.EGraph.NewRule("overflows", true);
            var x = rb.NewVar(ColumnTy.Id);
            rb.QueryTable(g.Mark, [x, rb.NewVar(g.UnitType)], false);
            var passed = rb.CallExternalFunc(overflow, [x], ColumnTy.Id, () => "overflow");
            rb.Set(g.Path, [passed, passed, RuleEntry.Const(g.Unit, g.UnitType)]);
            var rule = rb.Build();
            g.Assert(g.Root, g.Mark, g.Root.FreshId());
            g.Root.FlushUpdates();
            g.Root.Freeze();
            using var child = g.Root.CreateChild();

            Assert.Throws<OverflowException>(() => child.RunRules(rule));
            Assert.False(g.EGraph.Store.IdsExhausted);
            Assert.True(child.IsFaulted);
        }

        /// <summary>
        /// A child whose parent unioned since it last caught up, rebuilt once the clock has run out, throws
        /// <see cref="OverflowException"/> before its first pass and is not faulted.
        /// </summary>
        [Fact]
        public void AChildRebuiltOnceTheClockHasRunOutIsNotFaulted()
        {
            var g = new ContextGraph();
            var a = g.Root.FreshId();
            var b = g.Root.FreshId();
            g.Assert(g.Root, g.Mark, a);
            g.Assert(g.Root, g.Mark, b);
            g.Root.FlushUpdates();
            using var child = g.Root.CreateChild();
            g.Root.StageUnion(a, b);
            g.Root.FlushUpdates();
            var store = g.EGraph.Store;
            store.Counters.IncrementBy(store.ClockCounter, Value.StaleRep - store.Counters.Read(store.ClockCounter));

            Assert.Throws<OverflowException>(() => child.Context.Rebuild());
            Assert.False(child.IsFaulted);
            Assert.Throws<OverflowException>(() => child.Context.Rebuild());
            Assert.False(child.IsFaulted);
        }

        /// <summary>
        /// A child whose flush merged a union with the clock's last epoch and then ran out before its rebuild is left
        /// unclean; freezing it throws <see cref="OverflowException"/> from the rebuild the freeze makes first, and
        /// leaves it neither frozen nor faulted.
        /// </summary>
        [Fact]
        public void AChildFrozenOnceTheClockHasRunOutIsNotFaulted()
        {
            var g = new ContextGraph();
            var a = g.Root.FreshId();
            var b = g.Root.FreshId();
            g.Assert(g.Root, g.Mark, a);
            g.Assert(g.Root, g.Mark, b);
            g.Root.FlushUpdates();
            g.Root.Freeze();
            using var child = g.Root.CreateChild();
            var store = g.EGraph.Store;
            store.Counters.IncrementBy(store.ClockCounter, Value.StaleRep - 1 - store.Counters.Read(store.ClockCounter));
            child.StageUnion(a, b);

            Assert.Throws<OverflowException>(() => child.FlushUpdates());
            Assert.False(child.IsFaulted);
            Assert.False(child.Context.IsClean);
            Assert.Throws<OverflowException>(() => child.Context.Freeze());
            Assert.False(child.IsFaulted);
            Assert.False(child.Context.IsFrozen);
            Assert.Throws<OverflowException>(() => child.Context.Freeze());
            Assert.False(child.IsFaulted);
        }

        /// <summary>
        /// A child whose rebuild runs out of the clock after its first pass, on its own union or in a catch-up with a
        /// writable root, is left unfaulted with that pass ended as the loop ends it: its cursors committed and its hidden
        /// sets sealed. The pass left nothing to do, so the child is clean, its invariants hold, and it reads without an
        /// epoch.
        /// </summary>
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void AChildWhoseRebuildRunsOutBetweenPassesEndsThePassesThatRan(bool catchUp)
        {
            var g = new ContextGraph();
            var a = g.Root.FreshId();
            var b = g.Root.FreshId();
            g.Assert(g.Root, g.Mark, a);
            g.Assert(g.Root, g.Mark, b);
            g.Root.FlushUpdates();
            if (catchUp == false)
                g.Root.Freeze();

            using var child = g.Root.CreateChild();
            if (catchUp)
            {
                g.Root.StageUnion(a, b);
                g.Root.FlushUpdates();
                LeaveEpochs(g.EGraph.Store, 1);
                Assert.Throws<OverflowException>(() => child.Context.Rebuild());
            }
            else
            {
                LeaveEpochs(g.EGraph.Store, 2);
                child.StageUnion(a, b);
                Assert.Throws<OverflowException>(() => child.FlushUpdates());
            }

            Assert.Equal(1, child.Context.LastRebuildPasses);
            Assert.False(child.IsFaulted);
            Assert.Equal(0, child.Context.Tables!.UnsealedCount);
            Assert.True(child.Context.IsClean);
            child.Context.CheckInvariants();
            Assert.Single(ContextGraph.Keys(child, g.Mark));
            Assert.Equal(child.Find(a), child.Find(b));
        }

        /// <summary>
        /// A parallel child search that runs out of ids after many action batches flushed leaves the child unfaulted,
        /// with nothing it staged left behind, in a layer the search created and in one the child had before: a later
        /// flush merges nothing of the failed search.
        /// </summary>
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void AParallelChildSearchThatRunsOutOfIdsLeavesNothingStaged(bool layerExists)
        {
            var g = new ContextGraph(new ParallelHeuristics(4));
            const int N = 1000;
            var store = g.EGraph.Store;
            var calls = 0;
            var gate = Register(g.EGraph, (_, args) =>
            {
                if (Interlocked.Increment(ref calls) <= N - 50)
                    return args[0];

                // A mint past the last id: the counter moves past the limit, and the mint throws.
                store.Counters.IncrementBy(store.IdCounter, Store.IdLimit + 1);
                throw new OverflowException("The store has run out of ids.");
            });
            var rb = g.EGraph.NewRule("gated", true);
            var x = rb.NewVar(ColumnTy.Id);
            rb.QueryTable(g.Mark, [x, rb.NewVar(g.UnitType)], false);
            var passed = rb.CallExternalFunc(gate, [x], ColumnTy.Id, () => "gate");
            rb.Set(g.Path, [passed, passed, RuleEntry.Const(g.Unit, g.UnitType)]);
            var rule = rb.Build();
            for (var i = 0; i < N; i++)
                g.Assert(g.Root, g.Mark, g.Root.FreshId());

            var kept = g.Root.FreshId();
            g.Root.FlushUpdates();
            g.Root.Freeze();
            using var child = g.Root.CreateChild(new ContextOptions { Parallelism = new ParallelHeuristics(4) });
            child.Context.Database.ParallelRuleSetsOverride = true;
            if (layerExists)
            {
                child.StageRow(g.Path, [kept, kept, g.Unit]);
                child.FlushUpdates();
            }

            var before = child.TableSize(g.Path);
            Assert.Throws<OverflowException>(() => child.RunRules(rule));
            Assert.False(child.IsFaulted);
            Assert.True(store.IdsExhausted);
            Assert.True(calls > N - 50);
            child.FlushUpdates();
            Assert.Equal(before, child.TableSize(g.Path));
        }

        /// <summary>
        /// A child's run that fails in its merge, not its search, still faults the child: here a lattice merge that
        /// throws while the rows of a raw rule set's run merge.
        /// </summary>
        [Fact]
        public void AChildRunThatFailsInItsMergeFaultsTheChild()
        {
            var g = new ContextGraph();
            var failing = false;
            var boom = Register(g.EGraph, (_, args) => failing ? throw new InvalidOperationException("merge failed") : args[0]);
            var boomy = g.EGraph.AddTable(new FunctionConfig
            {
                Schema = [ColumnTy.Id, g.I64],
                Default = DefaultVal.Fail,
                Merge = MergeFn.Primitive(boom, MergeFn.Old, MergeFn.New).AsLattice(),
                Name = "boomy",
            });
            var a = g.Root.FreshId();
            g.Assert(g.Root, g.Mark, a);
            g.Root.AddValues([(boomy, [a, g.Int(1)])]);
            g.Root.FlushUpdates();
            g.Root.Freeze();

            using var child = g.Root.CreateChild();
            var database = child.Context.Database;
            var rsb = database.NewRuleSet();
            var q = rsb.NewRule();
            var v = q.NewVariable("v");
            var u = q.NewVariable("u");
            var t = q.NewVariable("t");
            q.AddAtom(g.EGraph.GetTable(g.Mark), [v, u, t]);
            var r = q.Build();
            var ts = r.ReadCounter(g.EGraph.TimestampCounter);
            r.Insert(g.EGraph.GetTable(boomy), [v, QueryEntry.Const(g.Int(2)), ts]);
            r.Build("writes boomy");
            using var ruleSet = rsb.Build();
            failing = true;

            Assert.Throws<InvalidOperationException>(() => database.RunRuleSet(ruleSet, ReportLevel.TimeOnly, g.EGraph.NextTimestamp));
            Assert.True(child.IsFaulted);
        }

        /// <summary>
        /// A clone of a store's root database keeps the root's id bound: its rule mints stop at
        /// <see cref="UnionFind.MaxId"/> as the root's do.
        /// </summary>
        [Fact]
        public void ACloneOfTheRootDatabaseKeepsTheIdBound()
        {
            var store = new Store();
            var clone = store.Root.Database.Clone();
            clone.Counters.IncrementBy(store.IdCounter, UnionFind.MaxId - clone.ReadCounter(store.IdCounter));

            Assert.Equal(UnionFind.MaxId, clone.IncrementCounter(store.IdCounter));
            Assert.Throws<OverflowException>(() => clone.IncrementCounter(store.IdCounter));
        }

        /// <summary>
        /// Moves the store's clock so that <paramref name="left"/> epochs remain.
        /// </summary>
        static void LeaveEpochs(Store store, long left)
        {
            store.Counters.IncrementBy(store.ClockCounter, Value.StaleRep - left - store.Counters.Read(store.ClockCounter));
        }

    }

}

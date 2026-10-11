using System;
using System.Collections.Generic;
using System.Threading;

using Xunit;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// Tests for the lifecycle of contexts in non-frozen chains (colored-contexts spec §8.2, §8.3, X15, X36): creation,
    /// disposal rules, descendant gates, the depth limit, the shared thread domain, and the refusal of a second thread
    /// and of re-entrant structural calls. Domains under frozen parents arrive with freezing (M9.2).
    /// </summary>
    public class ContextLifecycleTests
    {

        [Fact]
        public void CreateChildBuildsTheChain()
        {
            var store = new Store();
            var root = store.Root;
            store.AdvanceClock();
            var child = root.CreateChild();
            var grandchild = child.CreateChild(ContextOptions.Case);

            Assert.Equal(0, root.Depth);
            Assert.Equal(1, child.Depth);
            Assert.Equal(2, grandchild.Depth);
            Assert.Null(root.Parent);
            Assert.Same(root, child.Parent);
            Assert.Same(child, grandchild.Parent);
            Assert.Equal(new[] { root, child, grandchild }, grandchild.Ancestors.ToArray());
            Assert.Equal(new[] { root }, root.Ancestors.ToArray());
            Assert.True(root.IsRoot);
            Assert.False(child.IsRoot);
            Assert.Same(ContextOptions.Default, child.Options);
            Assert.Same(ContextOptions.Case, grandchild.Options);
            Assert.True(grandchild.Options.StopOnContradiction);
            Assert.True(grandchild.Options.IsCase);
            Assert.False(child.Options.StopOnContradiction);
            Assert.True(child.Options.InheritWatermarks);
            Assert.Same(store, grandchild.Store);
            Assert.Equal(1, root.ChildCount);
            Assert.Equal(1, child.ChildCount);

            // CreatedAt is the clock at creation; the root's is 0, and the semi-naive floor starts there.
            Assert.Equal(0u, root.CreatedAt);
            Assert.Equal(2u, child.CreatedAt);
            Assert.Equal(2u, child.SemiNaiveFloor);
            child.NoteRuleRun(5);
            child.NoteRuleRun(3);
            Assert.Equal(5u, child.SemiNaiveFloor);

            // Each context owns its database; a child's shares the root's counters, functions, strata and containers.
            Assert.Same(child, child.Database.Owner);
            Assert.NotSame(root.Database, child.Database);
            Assert.Same(root.Database.Counters, child.Database.Counters);
            Assert.Same(root.Database.ExternalFunctions, grandchild.Database.ExternalFunctions);
            Assert.Same(root.Database.Dependencies, grandchild.Database.Dependencies);
            Assert.Same(root.Database.Containers, grandchild.Database.Containers);
            Assert.Same(root.Database.Parallelism, child.Database.Parallelism);
            Assert.Equal(store.ClockCounter.Rep, grandchild.Database.ClockCounterRep);
            Assert.False(child.IsFrozen);
            Assert.False(child.IsFaulted);
            Assert.False(child.IsContradicted);
        }

        [Fact]
        public void AChildTakesItsParallelismFromItsOptionsOrItsParent()
        {
            var store = new Store(new ParallelHeuristics(3));
            var inherit = store.Root.CreateChild();
            var custom = new ParallelHeuristics(2);
            var own = store.Root.CreateChild(new ContextOptions { Parallelism = custom });
            Assert.Same(store.Root.Database.Parallelism, inherit.Database.Parallelism);
            Assert.Same(custom, own.Database.Parallelism);
        }

        [Fact]
        public void DisposeRules()
        {
            var store = new Store();
            var root = store.Root;
            var child = root.CreateChild();
            var grandchild = child.CreateChild();

            Assert.Throws<InvalidOperationException>(() => root.Dispose());
            Assert.Throws<InvalidOperationException>(() => child.Dispose());
            Assert.False(child.IsDisposed);

            grandchild.Dispose();
            Assert.True(grandchild.IsDisposed);
            Assert.Equal(0, child.ChildCount);
            grandchild.Dispose();

            child.Dispose();
            Assert.True(child.IsDisposed);
            Assert.Equal(0, root.ChildCount);
            Assert.Throws<ObjectDisposedException>(() => child.CreateChild());
            Assert.Throws<ObjectDisposedException>(() => child.NewId());
            Assert.Throws<ObjectDisposedException>(() => child.IsLocalId(1));
            Assert.Throws<ObjectDisposedException>(() => child.BeginEpoch());

            // The parent goes on, and a fresh child of it starts clean.
            var again = root.CreateChild();
            Assert.Equal(1, root.ChildCount);
            again.Dispose();
        }

        [Fact]
        public void HostIsSetOnceAndReleasedOnDispose()
        {
            var store = new Store();
            var child = store.Root.CreateChild();
            var host = new object();
            child.Host = host;
            child.Host = host;
            Assert.Same(host, child.Host);
            Assert.Throws<InvalidOperationException>(() => child.Host = new object());
            child.Dispose();
            Assert.Null(child.Host);
        }

        [Fact]
        public void GatesCountLiveDescendantsAndGuardClone()
        {
            var store = new Store();
            var root = store.Root;
            Assert.False(root.Gate.HasLiveDescendants);
            var a = root.CreateChild();
            var b = a.CreateChild();
            var c = root.CreateChild();

            Assert.Equal(3, root.Gate.LiveDescendants);
            Assert.Equal(1, a.Gate.LiveDescendants);
            Assert.Equal(0, b.Gate.LiveDescendants);
            Assert.True(root.Gate.HasLiveDescendants);
            Assert.False(c.Gate.HasLiveDescendants);

            // A root with live descendants, and any child, cannot be cloned (§4.3).
            Assert.Throws<InvalidOperationException>(() => root.Database.Clone());
            Assert.Throws<InvalidOperationException>(() => b.Database.Clone());

            b.Dispose();
            Assert.Equal(2, root.Gate.LiveDescendants);
            Assert.Equal(0, a.Gate.LiveDescendants);
            a.Dispose();
            c.Dispose();
            Assert.Equal(0, root.Gate.LiveDescendants);
            Assert.False(root.Gate.HasLiveDescendants);

            var clone = root.Database.Clone();
            Assert.Null(clone.Owner);
            Assert.Equal(store.ClockCounter.Rep, clone.ClockCounterRep);
            Assert.Throws<InvalidOperationException>(() => new DescendantGate().Exit());
        }

        [Fact]
        public void DepthIsLimitedToThirtyOne()
        {
            var store = new Store();
            var chain = new List<Context> { store.Root };
            for (var i = 0; i < Context.MaxDepth; i++)
                chain.Add(chain[^1].CreateChild());

            Assert.Equal(31, chain[^1].Depth);
            Assert.Equal(31, store.Root.Gate.LiveDescendants);
            Assert.Throws<InvalidOperationException>(() => chain[^1].CreateChild());
            Assert.Equal(0, chain[^1].ChildCount);

            for (var i = chain.Count - 1; i > 0; i--)
                chain[i].Dispose();

            Assert.Equal(0, store.Root.Gate.LiveDescendants);
        }

        [Fact]
        public void ANonFrozenChainSharesOneDomain()
        {
            var store = new Store();
            var child = store.Root.CreateChild();
            var grandchild = child.CreateChild();
            var sibling = store.Root.CreateChild();
            Assert.Same(store.Root.Domain, child.Domain);
            Assert.Same(store.Root.Domain, grandchild.Domain);
            Assert.Same(store.Root.Domain, sibling.Domain);
            Assert.False(store.Root.Domain.IsHeld);
        }

        [Fact]
        public void TwoThreadsOnOneDomainThrow()
        {
            var store = new Store();
            var child = store.Root.CreateChild();
            var grandchild = child.CreateChild();
            using var held = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();

            // One thread holds the domain through the grandchild, which shares it with the whole chain.
            var owner = ConcurrencyTestThread.Start(() =>
            {
                using (grandchild.Domain.Enter())
                {
                    held.Set();
                    release.Wait();
                }
            });

            held.Wait();
            try
            {
                Assert.Throws<InvalidOperationException>(() => store.Root.CreateChild());
                Assert.Throws<InvalidOperationException>(() => child.CreateChild());
                Assert.Throws<InvalidOperationException>(() => grandchild.Dispose());
                Assert.Throws<InvalidOperationException>(() => store.AddTable(StoreTests.Function(store)));
                Assert.False(grandchild.IsDisposed);
                Assert.Equal(1, child.ChildCount);
            }
            finally
            {
                release.Set();
                owner.Join();
            }

            // Once the domain is free, the same calls succeed on this thread.
            grandchild.Dispose();
            child.CreateChild().Dispose();
        }

        [Fact]
        public void ReentrantCreateChildAndDisposeThrow()
        {
            var store = new Store();
            var child = store.Root.CreateChild();
            var grandchild = child.CreateChild();

            // Inside an operation of the domain (a write, an action, a visitor) structural calls are refused.
            using (store.Root.Domain.Enter())
            {
                Assert.Throws<InvalidOperationException>(() => store.Root.CreateChild());
                Assert.Throws<InvalidOperationException>(() => child.CreateChild());
                Assert.Throws<InvalidOperationException>(() => grandchild.Dispose());
                Assert.False(grandchild.IsDisposed);
                Assert.Equal(1, store.Root.Domain.Depth);

                // Reads and mints stay allowed.
                Assert.True(grandchild.IsLocalId(grandchild.NewId()));
            }

            Assert.Equal(0, store.Root.Domain.Depth);
            grandchild.Dispose();
            child.Dispose();
        }

        [Fact]
        public void VersionMovesWithMergesThatChangeRowsAndEffectiveClears()
        {
            var store = new Store();
            var root = store.Root;
            var db = root.Database;
            var f = store.AddTable(StoreTests.Function(store), [], [store.UnionFindTable], "f");
            Assert.Equal(0, root.Version);

            db.WithExecutionState(s => { s.StageInsert(f, [1, 10, 1]); return 0; });
            db.MergeAll(1);
            var afterInsert = root.Version;
            Assert.True(afterInsert > 0);

            // A merge that changes nothing, and a redundant union, leave it alone (D16).
            db.WithExecutionState(s => { s.StageInsert(f, [1, 10, 1]); return 0; });
            db.WithExecutionState(s => { s.StageInsert(store.UnionFindTable, [3, 3, 1]); return 0; });
            db.MergeAll(1);
            Assert.Equal(afterInsert, root.Version);

            // An effective union moves it; so does a clear that removes rows, but not an empty clear.
            db.WithExecutionState(s => { s.StageInsert(store.UnionFindTable, [3, 4, 2]); return 0; });
            db.MergeTable(store.UnionFindTable, 2);
            var afterUnion = root.Version;
            Assert.True(afterUnion > afterInsert);
            db.ClearTable(f);
            var afterClear = root.Version;
            Assert.True(afterClear > afterUnion);
            db.ClearTable(f);
            Assert.Equal(afterClear, root.Version);
        }

    }

}

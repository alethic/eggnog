using System;
using System.Collections.Generic;
using System.Threading;

using Xunit;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// Tests for the lifecycle of contexts in non-frozen chains: creation, disposal rules, descendant gates, the depth
    /// limit, the shared thread domain, and the refusal of a second thread and of re-entrant structural calls; and the
    /// domains of children under frozen parents.
    /// </summary>
    public class ContextLifecycleTests
    {

        /// <summary>
        /// Children link to their parents with depths, ancestors, options and creation epochs; each context owns its
        /// database, which shares the root's store-wide registries.
        /// </summary>
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

        /// <summary>
        /// A child uses the parallelism its options name, or else its parent's.
        /// </summary>
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

        /// <summary>
        /// The root and a context with live children cannot be disposed; disposing a leaf is idempotent, detaches it
        /// from its parent, and makes its members throw <see cref="ObjectDisposedException"/>.
        /// </summary>
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

        /// <summary>
        /// A context's host may be set again to the same object but not replaced, and disposal releases it.
        /// </summary>
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

        /// <summary>
        /// Each context's gate counts its live descendants as they are created and disposed, and a database can be
        /// cloned only at a root without live descendants.
        /// </summary>
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

            // A root with live descendants, and any child, cannot be cloned.
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

        /// <summary>
        /// A chain reaches <see cref="Context.MaxDepth"/> (31) below the root, and the deepest context refuses a child.
        /// </summary>
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

        /// <summary>
        /// Every context of a chain with no frozen context shares the root's thread domain.
        /// </summary>
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

        /// <summary>
        /// While one thread holds a chain's domain, structural calls from another thread on any context of the chain
        /// throw and change nothing; once the domain is free they succeed.
        /// </summary>
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

        /// <summary>
        /// Children of a frozen context get domains of their own, so siblings run on different threads at once;
        /// freezing gives each live child subtree a new domain; a frozen context is read without its domain.
        /// </summary>
        [Fact]
        public void FrozenParentDomains()
        {
            var store = new Store();
            var root = store.Root;
            var ids = new[] { root.NewId(), root.NewId(), root.NewId(), root.NewId() };
            var early = root.CreateChild();
            var earlyChild = early.CreateChild();
            var rootDomain = root.Domain;
            Assert.Same(rootDomain, early.Domain);
            Assert.Same(rootDomain, earlyChild.Domain);

            // Freezing gives each live child subtree a domain of its own, shared within the subtree.
            root.Freeze();
            Assert.Same(rootDomain, root.Domain);
            Assert.NotSame(rootDomain, early.Domain);
            Assert.Same(early.Domain, earlyChild.Domain);

            // Children of a frozen context each get their own domain; a grandchild of one that is not frozen shares it.
            var a = root.CreateChild();
            var b = root.CreateChild();
            Assert.NotSame(a.Domain, b.Domain);
            Assert.NotSame(rootDomain, a.Domain);
            Assert.NotSame(early.Domain, a.Domain);
            var a1 = a.CreateChild();
            Assert.Same(a.Domain, a1.Domain);
            var aDomain = a.Domain;
            a.Freeze();
            Assert.Same(aDomain, a.Domain);
            Assert.NotSame(aDomain, a1.Domain);

            // Siblings under a frozen parent run concurrently: while one thread holds b's domain, this thread works in
            // early's subtree and in a1, and reads the frozen root, but b itself is refused.
            using var held = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            var owner = ConcurrencyTestThread.Start(() =>
            {
                using (b.Domain.Enter())
                {
                    held.Set();
                    release.Wait();
                }
            });

            held.Wait();
            try
            {
                Assert.True(earlyChild.Union(ids[1], ids[0]));
                earlyChild.Rebuild();
                Assert.Equal(ids[0], earlyChild.Find(ids[1]));
                Assert.True(a1.Union(ids[3], ids[2]));
                a1.Rebuild();
                Assert.Equal(ids[2], a1.Find(ids[3]));
                Assert.Equal(ids[1], root.Find(ids[1]));
                Assert.Throws<InvalidOperationException>(() => b.Union(ids[0], ids[2]));
                Assert.Throws<InvalidOperationException>(() => b.CreateChild());
            }
            finally
            {
                release.Set();
                owner.Join();
            }

            Assert.True(b.Union(ids[0], ids[2]));
            Assert.False(b.IsFaulted);
        }

        /// <summary>
        /// Inside an operation of the domain, creating and disposing contexts is refused; reads and mints are allowed.
        /// </summary>
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

        /// <summary>
        /// A context's version moves on every effective change (a merge that changes rows, an effective union, a clear
        /// that removes rows) and on nothing else.
        /// </summary>
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

            // A merge that changes nothing, and a redundant union, leave it alone.
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

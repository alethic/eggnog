using System;

using Xunit;

using static Eggnog.Relations.Tests.LayeredTestUtil;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// Tests for the contradiction stop at the storage level: with <see cref="ContextOptions.StopOnContradiction"/> a
    /// merged contradiction row stops the context, its merge loop stops after the round, and it answers only
    /// <see cref="Context.IsContradictory"/>; without it a contradiction is an ordinary fact; and a child that stops on
    /// contradictions, created where a contradiction is visible, starts stopped.
    /// </summary>
    public class ContradictionTests
    {

        static (Store Store, TableId Bottom, TableId G) Setup()
        {
            var store = new Store(ParallelHeuristics.Serial);
            var bottom = store.AddTable(Relation(0), name: "bottom");
            store.DesignateContradiction(bottom);
            var g = store.AddTable(Constructor(store), [], [store.UnionFindTable], "g");
            return (store, bottom, g);
        }

        [Fact]
        public void AStoppingChildStopsWhenAContradictionMerges()
        {
            var (store, bottom, g) = Setup();
            var root = store.Root;
            var a = root.NewId();
            var x = root.NewId();
            var y = root.NewId();
            Insert(root, g, a, x, 0);
            root.Freeze();
            var child = root.CreateChild(new ContextOptions { StopOnContradiction = true });
            Assert.False(child.IsContradicted);
            Assert.False(child.IsContradictory);
            var version = child.Version;

            // One batch: the contradiction and a constructor conflict whose union would merge in the next round.
            child.Write(state =>
            {
                state.StageInsert(bottom, [state.Timestamp]);
                state.StageInsert(g, [a, y, state.Timestamp]);
                return 0;
            });

            Assert.True(child.IsContradicted);
            Assert.True(child.IsContradictory);
            Assert.True(child.Version > version);

            // The merge stopped before the union-find's round: the union stays pending, and the epilogue still sealed.
            Assert.Equal(0, child.DisplacedCount);
            Assert.False(child.Database.Catalog.Notifications.IsEmpty);
            Assert.Equal(0, child.Tables!.UnsealedCount);

            // Only IsContradictory may be read; writes, layers and rebuilds stop.
            Assert.Throws<InvalidOperationException>(() => child.GetView(g));
            Assert.Throws<InvalidOperationException>(() => child.Find(a));
            Assert.Throws<InvalidOperationException>(() => Insert(child, g, x, x, 0));
            Assert.Throws<InvalidOperationException>(() => child.Tables.GetOrCreateLayer(store.AddTable(MaxFunction(1))));
            Assert.Throws<InvalidOperationException>(() => child.CreateChild());
            Assert.False(child.Rebuild());
            Assert.False(child.IsFaulted);
            Assert.True(child.IsContradictory);
            child.Dispose();
        }

        [Fact]
        public void WithoutTheOptionAContradictionIsAnOrdinaryFact()
        {
            var (store, bottom, _) = Setup();
            var root = store.Root;
            root.Freeze();
            var child = root.CreateChild();
            Insert(child, bottom, 0);
            Assert.False(child.IsContradicted);
            Assert.True(child.IsContradictory);
            Assert.NotNull(Row(child, bottom));
            Assert.Equal(1, child.GetView(bottom).VisibleCount);
            Assert.False(root.IsContradictory);
            LookupPaths.Check(child);

            // A child that stops on contradictions, created under it, starts stopped; an ordinary child does not.
            child.Freeze();
            var stopping = child.CreateChild(new ContextOptions { StopOnContradiction = true });
            Assert.True(stopping.IsContradicted);
            Assert.True(stopping.IsContradictory);
            Assert.Throws<InvalidOperationException>(() => stopping.GetView(bottom));
            var ordinary = child.CreateChild();
            Assert.False(ordinary.IsContradicted);
            Assert.True(ordinary.IsContradictory);
        }

        /// <summary>
        /// The children of a context that a contradiction stopped with writes pending: its parent is never clean again,
        /// and a child catches up only with a parent that is frozen or clean, so it never catches up with that parent.
        /// A child that stops on contradictions stops too (the contradiction row is visible in its parent), which moves
        /// its change stamp; any other child is refused on rebuild, write and child creation, unfaulted and without
        /// taking an epoch; both may be disposed.
        /// </summary>
        [Fact]
        public void TheChildrenOfAStoppedContextStopOrAreRefused()
        {
            var (store, bottom, g) = Setup();
            var root = store.Root;
            var a = root.NewId();
            var x = root.NewId();
            var y = root.NewId();
            Insert(root, g, a, x, 0);
            root.Freeze();
            var stopping = root.CreateChild(new ContextOptions { StopOnContradiction = true });
            var ordinary = stopping.CreateChild();
            var inner = stopping.CreateChild(new ContextOptions { StopOnContradiction = true });
            var grandchild = ordinary.CreateChild();

            stopping.Write(state =>
            {
                state.StageInsert(bottom, [state.Timestamp]);
                state.StageInsert(g, [a, y, state.Timestamp]);
                return 0;
            });
            Assert.True(stopping.IsContradicted);
            Assert.False(stopping.IsClean);

            var clock = store.ReadClock();
            Assert.Throws<InvalidOperationException>(() => ordinary.Rebuild());
            Assert.Throws<InvalidOperationException>(() => Insert(ordinary, g, x, x, 0));
            Assert.Throws<InvalidOperationException>(() => ordinary.CreateChild());
            Assert.Throws<InvalidOperationException>(() => grandchild.Rebuild());
            Assert.False(ordinary.IsFaulted);
            Assert.False(grandchild.IsFaulted);
            Assert.False(ordinary.IsClean);

            var before = inner.Version;
            Assert.False(inner.Rebuild());
            Assert.True(inner.IsContradicted);
            Assert.True(inner.Version > before, "the stop moved the change stamp");
            Assert.True(inner.IsContradictory);
            Assert.Throws<InvalidOperationException>(() => Insert(inner, g, x, x, 0));
            Assert.False(inner.IsFaulted);
            Assert.Equal(clock, store.ReadClock());

            grandchild.Dispose();
            ordinary.Dispose();
            inner.Dispose();
            stopping.Dispose();
        }

        [Fact]
        public void TheRootIsNeverStopped()
        {
            var (store, bottom, _) = Setup();
            var root = store.Root;
            Insert(root, bottom, 0);
            Assert.False(root.IsContradicted);
            Assert.True(root.IsContradictory);
        }

    }

}

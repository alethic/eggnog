using System;
using System.Linq;

using Xunit;

namespace Eggnog.Tests.Scopes
{

    /// <summary>
    /// What <see cref="Scope.CompactionsBlockedAtFreeze"/> reports: a scope frozen with a live descendant keeps the
    /// replaced rows of its tables and counts each such table; a scope frozen without descendants, or whose descendants
    /// were all disposed first, compacts and counts none. The root and a scope below a frozen root are checked alike.
    /// </summary>
    public sealed class CompactionAtFreezeTests
    {

        /// <summary>
        /// The keys each scope writes.
        /// </summary>
        const int Keys = 64;

        /// <summary>
        /// The first keys written again with a larger value. Their replaced rows stay below the automatic compaction's
        /// threshold (more than half the physical rows), so only the freeze would remove them.
        /// </summary>
        const int Replaced = 30;

        [Fact]
        public void ARootFrozenWithALiveChildReportsItsBlockedCompaction()
        {
            var (graph, level) = Define();
            var root = graph.Root;
            Write(root, level);
            var child = root.CreateChild();
            Assert.Equal(0, root.CompactionsBlockedAtFreeze);

            root.Freeze();

            Assert.Equal(1, root.CompactionsBlockedAtFreeze);
            Assert.Equal(0, child.CompactionsBlockedAtFreeze);
            Assert.Equal(2L, child.Get(level.Of("k0")));
            Assert.Equal(1L, child.Get(level.Of($"k{Keys - 1}")));
            Assert.Equal(Keys, child.CountRows(level));
        }

        [Fact]
        public void ARootFrozenWithoutChildrenReportsNone()
        {
            var (graph, level) = Define();
            Write(graph.Root, level);

            graph.Root.Freeze();

            Assert.Equal(0, graph.Root.CompactionsBlockedAtFreeze);
            Assert.Equal(2L, graph.Root.Get(level.Of("k0")));
            Assert.Equal(Keys, graph.Root.CountRows(level));
        }

        [Fact]
        public void ARootWhoseChildrenWereDisposedBeforeTheFreezeReportsNone()
        {
            var (graph, level) = Define();
            var root = graph.Root;
            Write(root, level);
            var child = root.CreateChild();
            var grandchild = child.CreateChild();
            grandchild.Dispose();
            child.Dispose();

            root.Freeze();

            Assert.Equal(0, root.CompactionsBlockedAtFreeze);
        }

        [Fact]
        public void ADisposedChildWithALiveGrandchildStillBlocksTheFreeze()
        {
            var (graph, level) = Define();
            var root = graph.Root;
            Write(root, level);
            var child = root.CreateChild();
            var grandchild = child.CreateChild();
            child.Dispose();
            Assert.Empty(root.Children);

            root.Freeze();

            Assert.Equal(1, root.CompactionsBlockedAtFreeze);
            Assert.Equal(2L, grandchild.Get(level.Of("k0")));
            Assert.Equal(Keys, grandchild.CountRows(level));
        }

        [Fact]
        public void ABlockOnlyWhileAChildLivedIsNotCountedAtALaterFreeze()
        {
            var (graph, level) = Define();
            var root = graph.Root;
            root.Assume(Enumerable.Range(0, Keys).Select(i => level.Of($"k{i}").Is(1L)).ToArray());
            var child = root.CreateChild();

            // The third raise leaves 128 tombstones in 192 physical rows, more than half of them, so the automatic
            // compaction triggers and the live child blocks it.
            root.Assume(Enumerable.Range(0, Keys).Select(i => level.Of($"k{i}").Is(2L)).ToArray());
            root.Assume(Enumerable.Range(0, Keys).Select(i => level.Of($"k{i}").Is(3L)).ToArray());
            var table = Table(graph, level);
            Assert.Equal(1, table.CompactionsBlocked);
            Assert.Equal(2 * Keys, table.StaleCount);
            child.Dispose();

            root.Freeze();

            Assert.Equal(0, root.CompactionsBlockedAtFreeze);
            Assert.Equal(0, table.StaleCount);
            Assert.Equal(3L, root.Get(level.Of("k0")));
            Assert.Equal(Keys, root.CountRows(level));
        }

        [Fact]
        public void ATableBlockedBeforeAndAtTheFreezeIsCountedOnce()
        {
            var (graph, level) = Define();
            var root = graph.Root;
            root.Assume(Enumerable.Range(0, Keys).Select(i => level.Of($"k{i}").Is(1L)).ToArray());
            var child = root.CreateChild();

            // The third raise trips the automatic compaction under the live child, so the freeze is the table's second
            // blocked compaction.
            root.Assume(Enumerable.Range(0, Keys).Select(i => level.Of($"k{i}").Is(2L)).ToArray());
            root.Assume(Enumerable.Range(0, Keys).Select(i => level.Of($"k{i}").Is(3L)).ToArray());
            var table = Table(graph, level);
            Assert.Equal(1, table.CompactionsBlocked);

            root.Freeze();

            Assert.Equal(2, table.CompactionsBlocked);
            Assert.Equal(1, root.CompactionsBlockedAtFreeze);
            Assert.Equal(3L, child.Get(level.Of("k0")));
            Assert.Equal(Keys, child.CountRows(level));
        }

        [Fact]
        public void AScopeFrozenWithALiveChildReportsItsBlockedCompaction()
        {
            var (graph, level) = Define();
            graph.Root.Freeze();
            var schema = graph.Root.CreateChild();
            Write(schema, level);
            var query = schema.CreateChild();
            Assert.Equal(0, schema.CompactionsBlockedAtFreeze);

            schema.Freeze();

            Assert.Equal(1, schema.CompactionsBlockedAtFreeze);
            Assert.Equal(0, graph.Root.CompactionsBlockedAtFreeze);
            Assert.Equal(2L, query.Get(level.Of("k0")));
            Assert.Equal(Keys, query.CountRows(level));
        }

        [Fact]
        public void AScopeFrozenWithoutChildrenReportsNone()
        {
            var (graph, level) = Define();
            graph.Root.Freeze();
            var schema = graph.Root.CreateChild();
            Write(schema, level);

            schema.Freeze();

            Assert.Equal(0, schema.CompactionsBlockedAtFreeze);
            Assert.Equal(2L, schema.Get(level.Of("k0")));
            Assert.Equal(Keys, schema.CountRows(level));
        }

        [Fact]
        public void OnlyTheTablesWithReplacedRowsAreCounted()
        {
            var graph = new EGraph();
            var level = graph.DefineFunction<string, long>("level", ValueLattice.Max<long>());
            var other = graph.DefineFunction<string, long>("other", ValueLattice.Max<long>());
            var plain = graph.DefineRelation<string>("plain");
            var root = graph.Root;
            Write(root, level);
            Write(root, other);
            root.Assume(plain.Of("p"));
            var child = root.CreateChild();

            root.Freeze();

            Assert.Equal(2, root.CompactionsBlockedAtFreeze);
            GC.KeepAlive(child);
        }

        /// <summary>
        /// Defines a graph with one function under a maximum lattice, whose replaced values become replaced rows.
        /// </summary>
        static (EGraph Graph, Function<string, long> Level) Define()
        {
            var graph = new EGraph();
            return (graph, graph.DefineFunction<string, long>("level", ValueLattice.Max<long>()));
        }

        /// <summary>
        /// Gets the engine table that holds <paramref name="level"/>'s rows at the root.
        /// </summary>
        static Relations.SortedWritesTable Table(EGraph graph, Function<string, long> level) =>
            graph.Engine.Store.Schema[graph.Engine.GetTable(level.Function)]!.Root;

        /// <summary>
        /// Writes <see cref="Keys"/> keys with value 1 into <paramref name="scope"/>, then raises the first
        /// <see cref="Replaced"/> to 2, which replaces their rows.
        /// </summary>
        static void Write(Scope scope, Function<string, long> level)
        {
            scope.Assume(Enumerable.Range(0, Keys).Select(i => level.Of($"k{i}").Is(1L)).ToArray());
            scope.Assume(Enumerable.Range(0, Replaced).Select(i => level.Of($"k{i}").Is(2L)).ToArray());
        }

    }

}

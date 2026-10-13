using Xunit;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// Review probes for colors M10.3 (colored-contexts spec §10.4, §10.8, §6.7): how writes staged and not merged yet,
    /// bridge-side (<see cref="HostWrites"/>) or raw, interact with a later clear of the same function and with a freeze.
    /// </summary>
    /// <remarks>
    /// egglog's <c>clear_table</c> (egglog-bridge/src/lib.rs:409-424) drops "any rows staged for this table by an
    /// in-flight <c>MutationBuffer</c> ... along with the table contents", and <see cref="EGraph.ClearTable"/> documents
    /// the same: it "removes every row of <c>function</c>, and every write staged for it". Since M10.3 a context stages
    /// host rows bridge-side until its next flush (X24), so a clear issued after <see cref="EGraphContext.StageRow"/>
    /// must drop those rows too, or the operations take effect out of order: the clear first, the earlier write after it.
    /// </remarks>
    public class StagedWritesReviewTests
    {

        /// <summary>
        /// At the root: a row staged through the context API, then the egglog API's clear of its function, then a flush.
        /// The clear came last, so the function must be empty, as egglog's <c>clear_table</c> and the documentation of
        /// <see cref="EGraph.ClearTable"/> say.
        /// </summary>
        [Fact]
        public void TheRootsClearDropsRowsStagedBeforeIt()
        {
            var g = new ContextGraph();
            var a = g.Root.FreshId();
            var b = g.Root.FreshId();

            g.Assert(g.Root, g.Edge, a, b);
            g.EGraph.ClearTable(g.Edge);
            g.Root.FlushUpdates();

            Assert.Empty(ContextGraph.Keys(g.Root, g.Edge));
        }

        /// <summary>
        /// The same through the context API at the root: <see cref="EGraphContext.ClearTable"/> after
        /// <see cref="EGraphContext.StageRow"/> leaves the function empty once flushed.
        /// </summary>
        [Fact]
        public void TheContextApiClearDropsRowsStagedBeforeItAtTheRoot()
        {
            var g = new ContextGraph();
            var a = g.Root.FreshId();
            var b = g.Root.FreshId();

            g.Assert(g.Root, g.Edge, a, b);
            g.Root.ClearTable(g.Edge);
            g.Root.FlushUpdates();

            Assert.Empty(ContextGraph.Keys(g.Root, g.Edge));
        }

        /// <summary>
        /// In a child of a frozen root, where a clear is allowed (§6.7): a row the child staged, then the child's clear of
        /// the function, then a flush. The child's view of the function must be empty.
        /// </summary>
        [Fact]
        public void AChildsClearDropsRowsItStagedBeforeIt()
        {
            var g = new ContextGraph();
            var a = g.Root.FreshId();
            var b = g.Root.FreshId();
            var c = g.Root.FreshId();
            g.Assert(g.Root, g.Edge, a, c);
            g.Root.FlushUpdates();
            g.Root.Freeze();

            using var child = g.Root.CreateChild();
            g.Assert(child, g.Edge, a, b);
            child.ClearTable(g.Edge);
            child.FlushUpdates();

            Assert.Empty(ContextGraph.Keys(child, g.Edge));
        }

        /// <summary>
        /// §10.8 step 1 flushes before the freeze. The bridge skips that flush when nothing is staged bridge-side, relying on
        /// the storage freeze to merge raw pending writes: a row staged at the root through
        /// <see cref="EGraph.WithExecutionState{TResult}"/> and not flushed must be in the frozen root, visible to a child.
        /// </summary>
        [Fact]
        public void FreezeMergesARowStagedRawAtTheRoot()
        {
            var g = new ContextGraph();
            var a = g.Root.FreshId();
            var b = g.Root.FreshId();
            var edge = new TableAction(g.EGraph, g.Edge);
            g.EGraph.WithExecutionState(state =>
            {
                edge.Insert(state, [a.Rep, b.Rep, g.Unit.Rep]);
                return 0;
            });

            g.Root.Freeze();
            Assert.Equal([$"{a.Rep} {b.Rep}"], ContextGraph.Keys(g.Root, g.Edge));
            using var child = g.Root.CreateChild();
            Assert.Equal([$"{a.Rep} {b.Rep}"], ContextGraph.Keys(child, g.Edge));
            g.Root.CheckInvariants();
        }

    }

}

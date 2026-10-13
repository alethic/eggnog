using Xunit;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// The raw escape (<c>WithExecutionState</c>, egglog's <c>with_execution_state</c>) against the context API's staged
    /// writes (colored-contexts spec §10.4): rows and unions staged earlier through <see cref="HostWrites"/> are flushed
    /// before the raw rows are staged, so writes land in the order the host issued them; under egglog's API alone the
    /// root's timestamp sequence is egglog's.
    /// </summary>
    public class RawWriteOrderTests
    {

        /// <summary>
        /// With nothing staged through the context API, the e-graph's raw escape does not advance the clock: one emptiness
        /// test, egglog's timestamp sequence.
        /// </summary>
        [Fact]
        public void TheRootsRawEscapeAloneDoesNotTickTheClock()
        {
            var g = new ContextGraph();
            var a = g.Root.FreshId();
            var b = g.Root.FreshId();
            var edge = new TableAction(g.EGraph, g.Edge);
            var before = g.EGraph.NextTimestamp;

            g.EGraph.WithExecutionState(state =>
            {
                edge.Insert(state, [a.Rep, b.Rep, g.Unit.Rep]);
                return 0;
            });

            Assert.Equal(before, g.EGraph.NextTimestamp);
            g.EGraph.FlushUpdates();
            Assert.Equal(before + 1, g.EGraph.NextTimestamp);
            Assert.Single(ContextGraph.Keys(g.Root, g.Edge));
        }

        /// <summary>
        /// A row staged at the root through the context API is merged before the raw escape runs, in an epoch of its own:
        /// the raw row then lands after it, and both are present after the flush.
        /// </summary>
        [Fact]
        public void TheRootsRawEscapeFlushesTheRowsStagedBeforeIt()
        {
            var g = new ContextGraph();
            var a = g.Root.FreshId();
            var b = g.Root.FreshId();
            var c = g.Root.FreshId();
            var edge = new TableAction(g.EGraph, g.Edge);

            g.Assert(g.Root, g.Edge, a, b);
            g.EGraph.WithExecutionState(state =>
            {
                edge.Insert(state, [a.Rep, c.Rep, g.Unit.Rep]);
                return 0;
            });
            Assert.Single(ContextGraph.Keys(g.Root, g.Edge));

            g.EGraph.FlushUpdates();
            Assert.Equal(2, ContextGraph.Keys(g.Root, g.Edge).Length);
            g.Root.CheckInvariants();
        }

        /// <summary>
        /// In a child of a frozen root, a row staged through the context API and a raw row for the same key of a lattice
        /// function both merge, and the child stays consistent: the raw escape flushes the staged row first.
        /// </summary>
        [Fact]
        public void AChildsRawEscapeFlushesTheRowsStagedBeforeIt()
        {
            var g = new ContextGraph();
            var a = g.Root.FreshId();
            g.Root.StageRow(g.Low, [a, g.Int(9)]);
            g.Root.FlushUpdates();
            g.Root.Freeze();

            using var child = g.Root.CreateChild();
            var low = new TableAction(g.EGraph, g.Low);
            child.StageRow(g.Low, [a, g.Int(5)]);
            child.WithExecutionState(state =>
            {
                low.Insert(state, [a.Rep, g.Int(7).Rep]);
                return 0;
            });
            child.FlushUpdates();
            Assert.Equal(g.Int(5), child.LookupId(g.Low, [a]));
            Assert.Equal(g.Int(9), g.Root.LookupId(g.Low, [a]));
            child.CheckInvariants();
        }

    }

}

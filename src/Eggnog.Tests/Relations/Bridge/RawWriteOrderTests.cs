using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Xunit;

using static Eggnog.Relations.Tests.BridgeTestUtil;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// The order in which a context's write paths land. The raw escape (<c>WithExecutionState</c>, egglog's
    /// <c>with_execution_state</c>) and the root's egglog-compatible <see cref="EGraph.AddValues"/> stage into database
    /// buffers, while the context API stages bridge-side (<see cref="HostWrites"/>) until the next flush replays its
    /// rows. Rows and unions staged earlier through the context API are flushed before a raw path stages, so writes
    /// land in the order the host issued them; under egglog's API alone the root's timestamp sequence is egglog's.
    /// </summary>
    public class RawWriteOrderTests
    {

        /// <summary>
        /// With nothing staged through the context API, the e-graph's raw escape does not advance the clock: one
        /// emptiness test, egglog's timestamp sequence.
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
        /// A row staged at the root through the context API is merged before the raw escape runs, in an epoch of its
        /// own: the raw row then lands after it, and both are present after the flush.
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
        /// In a child of a frozen root, a row staged through the context API and a raw row for the same key of a
        /// lattice function both merge, and the child stays consistent: the raw escape flushes the staged row first.
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

        /// <summary>
        /// Adds a function over one id key whose output is an i64 merged by <paramref name="merge"/>.
        /// </summary>
        static FunctionId ValueFunction(ContextGraph g, MergeFn merge, string name) => g.EGraph.AddTable(new FunctionConfig
        {
            Schema = [ColumnTy.Id, g.I64],
            Default = DefaultVal.Fail,
            Merge = merge,
            Name = name,
        });

        /// <summary>
        /// A row staged at the root through the context API, then a row for the same key staged raw through the
        /// e-graph's <c>WithExecutionState</c>, then a flush: the writes land in the order the host issued them, so
        /// under <c>:merge new</c> the raw row wins and under <c>:merge old</c> the staged one does.
        /// </summary>
        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void ARawWriteAfterAStagedRowLandsAfterIt(bool newWins)
        {
            var g = new ContextGraph();
            var f = ValueFunction(g, newWins ? MergeFn.New : MergeFn.Old, "f");
            var a = g.Root.FreshId();
            var action = new TableAction(g.EGraph, f);

            g.Root.StageRow(f, [a, g.Int(1)]);
            g.EGraph.WithExecutionState(state =>
            {
                action.Insert(state, [a.Rep, g.Int(2).Rep]);
                return 0;
            });
            g.EGraph.FlushUpdates();

            Assert.Equal(g.Int(newWins ? 2 : 1), g.Root.LookupId(f, [a]));
        }

        /// <summary>
        /// The same through the root context's own raw escape (<see cref="EGraphContext.WithExecutionState{TResult}"/>)
        /// and its <see cref="EGraphContext.FlushUpdates"/>.
        /// </summary>
        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void ARawContextWriteAfterAStagedRowLandsAfterIt(bool newWins)
        {
            var g = new ContextGraph();
            var f = ValueFunction(g, newWins ? MergeFn.New : MergeFn.Old, "f");
            var a = g.Root.FreshId();
            var action = new TableAction(g.EGraph, f);

            g.Root.StageRow(f, [a, g.Int(1)]);
            g.Root.WithExecutionState(state =>
            {
                action.Insert(state, [a.Rep, g.Int(2).Rep]);
                return 0;
            });
            g.Root.FlushUpdates();

            Assert.Equal(g.Int(newWins ? 2 : 1), g.Root.LookupId(f, [a]));
        }

        /// <summary>
        /// The control: a raw write first, then a row staged through the context API, then a flush. The raw row waits
        /// in a database buffer and the replay comes after it, so the order holds.
        /// </summary>
        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void AStagedRowAfterARawWriteLandsAfterIt(bool newWins)
        {
            var g = new ContextGraph();
            var f = ValueFunction(g, newWins ? MergeFn.New : MergeFn.Old, "f");
            var a = g.Root.FreshId();
            var action = new TableAction(g.EGraph, f);

            g.EGraph.WithExecutionState(state =>
            {
                action.Insert(state, [a.Rep, g.Int(1).Rep]);
                return 0;
            });
            g.Root.StageRow(f, [a, g.Int(2)]);
            g.EGraph.FlushUpdates();

            Assert.Equal(g.Int(newWins ? 2 : 1), g.Root.LookupId(f, [a]));
        }

        /// <summary>
        /// The control for <see cref="TheRootAppliesWritesInTheOrderTheyWereStaged"/>: two writes through one API land
        /// in staging order, so the order-dependent merges <c>new</c> and <c>old</c> keep the later and the earlier
        /// value.
        /// </summary>
        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void OneApiAppliesWritesInStagingOrder(bool newWins)
        {
            var g = new ContextGraph();
            var f = ValueFunction(g, newWins ? MergeFn.New : MergeFn.Old, "f");
            var a = g.Root.FreshId();

            g.Root.StageRow(f, [a, g.Int(1)]);
            g.Root.StageRow(f, [a, g.Int(2)]);
            g.Root.FlushUpdates();
            Assert.Equal(g.Int(newWins ? 2 : 1), g.Root.LookupId(f, [a]));

            var b = g.Root.FreshId();
            g.EGraph.AddValues([(f, [b, g.Int(1)]), (f, [b, g.Int(2)])]);
            Assert.Equal(g.Int(newWins ? 2 : 1), g.Root.LookupId(f, [b]));
        }

        /// <summary>
        /// The root's two write APIs are applied in the order the host issued them. A row staged at the root through
        /// the context API (bridge-side, <see cref="HostWrites"/>) and then a row for the same key loaded through the
        /// egglog-compatible <see cref="EGraph.AddValues"/>: under <c>:merge new</c> the later row wins, under
        /// <c>:merge old</c> the earlier one. <see cref="EGraphContext.AddValuesAtRoot"/> stages its rows into database
        /// buffers, so it flushes the root's host writes first; publishing its buffers before that flush replayed the
        /// rows staged earlier would land the earlier write last.
        /// </summary>
        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void TheRootAppliesWritesInTheOrderTheyWereStaged(bool newWins)
        {
            var g = new ContextGraph();
            var f = ValueFunction(g, newWins ? MergeFn.New : MergeFn.Old, "f");
            var a = g.Root.FreshId();

            g.Root.StageRow(f, [a, g.Int(1)]);
            g.EGraph.AddValues([(f, [a, g.Int(2)])]);

            Assert.True(g.Root.IsClean);
            Assert.Equal(g.Int(newWins ? 2 : 1), g.Root.LookupId(f, [a]));
        }

    }

}

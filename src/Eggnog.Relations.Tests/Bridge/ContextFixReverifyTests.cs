using Xunit;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// Second verification probes for the M10.3 fix stages (colored-contexts spec §10.4): the order in which the root's
    /// write paths land. The second fix stage made <see cref="EGraph.AddValues"/> and <see cref="EGraph.AddTerm"/> flush
    /// the rows staged earlier through the context API first; these probes cover the remaining raw path,
    /// <c>WithExecutionState</c> (egglog's <c>with_execution_state</c>), whose rows wait in database buffers while the
    /// context API's rows wait bridge-side until the flush replays them.
    /// </summary>
    public class ContextFixReverifyTests
    {

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
        /// e-graph's <c>WithExecutionState</c>, then a flush: the writes should land in the order the host issued them
        /// (§10.4), so under <c>:merge new</c> the raw row wins and under <c>:merge old</c> the staged one does.
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
        /// The control: a raw write first, then a row staged through the context API, then a flush. The raw row waits in
        /// a database buffer and the replay comes after it, so the order holds.
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
        /// The control for the clear fix on the raw path: a row staged raw at the root and not merged, then the e-graph's
        /// clear of its function, then a flush. egglog's <c>clear_table</c> drops what in-flight buffers staged for the
        /// table (lib.rs:409-424), so the function must be empty.
        /// </summary>
        [Fact]
        public void TheRootsClearDropsARowStagedRawBeforeIt()
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

            g.EGraph.ClearTable(g.Edge);
            g.EGraph.FlushUpdates();

            Assert.Empty(ContextGraph.Keys(g.Root, g.Edge));
        }

        /// <summary>
        /// The same in a child of a frozen root (§6.7 allows the clear there): a row the child staged raw, then the child's
        /// clear, then a flush. The child's view of the function must be empty.
        /// </summary>
        [Fact]
        public void AChildsClearDropsARowItStagedRawBeforeIt()
        {
            var g = new ContextGraph();
            var a = g.Root.FreshId();
            var b = g.Root.FreshId();
            var c = g.Root.FreshId();
            g.Assert(g.Root, g.Edge, a, c);
            g.Root.FlushUpdates();
            g.Root.Freeze();

            using var child = g.Root.CreateChild();
            var edge = new TableAction(g.EGraph, g.Edge);
            child.WithExecutionState(state =>
            {
                edge.Insert(state, [a.Rep, b.Rep, g.Unit.Rep]);
                return 0;
            });

            child.ClearTable(g.Edge);
            child.FlushUpdates();

            Assert.Empty(ContextGraph.Keys(child, g.Edge));
        }

    }

}

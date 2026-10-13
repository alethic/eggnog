using System;

using Xunit;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// Third verification probes for the M10.3 fix stages (colored-contexts spec §10.4): the raw escape
    /// (<c>WithExecutionState</c>) now enters the context's domain and flushes the context API's staged writes first.
    /// These probe what the raw escape does around that: on a frozen root, and when the action itself calls back into the
    /// context it runs in.
    /// </summary>
    public class RawEscapeReverifyTests
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
        /// The e-graph's raw escape on a frozen root: the egglog-compatible <see cref="EGraph.AddValues"/> refuses a frozen
        /// root, so the raw escape should too, rather than stage rows into the frozen database that no flush can ever
        /// merge (the root's flush refuses a frozen context).
        /// </summary>
        [Fact]
        public void TheRootsRawEscapeOnAFrozenRootIsRefusedLikeAddValues()
        {
            var g = new ContextGraph();
            var a = g.Root.FreshId();
            var b = g.Root.FreshId();
            g.Root.Freeze();
            using var child = g.Root.CreateChild();
            var edge = new TableAction(g.EGraph, g.Edge);

            Assert.Throws<InvalidOperationException>(() => g.EGraph.AddValues([(g.Edge, [a, b, g.Unit])]));
            Assert.Throws<InvalidOperationException>(() => g.EGraph.WithExecutionState(state =>
            {
                edge.Insert(state, [a.Rep, b.Rep, g.Unit.Rep]);
                return 0;
            }));
        }

        /// <summary>
        /// A row staged through the context API from inside the raw escape's action, before the action's own raw row for
        /// the same key: the writes should land in the order the host issued them (§10.4), so under <c>:merge new</c> the
        /// raw row, issued last, wins. The context's own storage-level escape (<c>Context.Write</c>) refuses changes from
        /// inside its action instead (§8.3); either is consistent, merging the earlier write last is not.
        /// </summary>
        [Fact]
        public void AStageFromInsideTheRawActionDoesNotLandAfterTheActionsLaterRow()
        {
            var g = new ContextGraph();
            var f = ValueFunction(g, MergeFn.New, "f");
            var a = g.Root.FreshId();
            var action = new TableAction(g.EGraph, f);

            Exception? refused = null;
            g.EGraph.WithExecutionState(state =>
            {
                try
                {
                    g.Root.StageRow(f, [a, g.Int(1)]);
                }
                catch (InvalidOperationException e)
                {
                    refused = e;
                }

                action.Insert(state, [a.Rep, g.Int(2).Rep]);
                return 0;
            });
            g.EGraph.FlushUpdates();

            if (refused is null)
                Assert.Equal(g.Int(2), g.Root.LookupId(f, [a]));
        }

        /// <summary>
        /// A rule run from inside the raw escape's action: the action's row is stamped with the clock read before the run,
        /// so once the run has committed its watermark the row lies below it when it merges, and no later run matches it
        /// (I31). The raw escape should refuse the run (as <c>Context.Write</c> refuses changes from its action, §8.3), or
        /// the row should still be matched.
        /// </summary>
        [Fact]
        public void ARunFromInsideTheRawActionDoesNotHideTheActionsRow()
        {
            var g = new ContextGraph();
            var a = g.Root.FreshId();
            var b = g.Root.FreshId();
            var c = g.Root.FreshId();
            var d = g.Root.FreshId();
            var rule = g.EdgeToPath();
            g.EGraph.AddValues([(g.Edge, [c, d, g.Unit])]);
            var edge = new TableAction(g.EGraph, g.Edge);

            Exception? refused = null;
            g.EGraph.WithExecutionState(state =>
            {
                edge.Insert(state, [a.Rep, b.Rep, g.Unit.Rep]);
                try
                {
                    g.EGraph.RunRules([rule]);
                    g.EGraph.RunRules([rule]);
                }
                catch (InvalidOperationException e)
                {
                    refused = e;
                }

                return 0;
            });

            for (var i = 0; i < 3; i++)
                g.EGraph.RunRules([rule]);

            if (refused is null)
            {
                Assert.Contains($"{a.Rep} {b.Rep}", ContextGraph.Keys(g.Root, g.Edge));
                Assert.Contains($"{a.Rep} {b.Rep}", ContextGraph.Keys(g.Root, g.Path));
            }
        }

        /// <summary>
        /// A row staged at the root through the context API, then a raw root iteration (<see cref="EGraph.RunIteration"/>,
        /// under <see cref="EGraph.RunRuleSet"/>) whose rule set writes the same key, then a flush: the writes should land
        /// in the order the host issued them (§10.4), so under <c>:merge new</c> the iteration's row wins.
        /// </summary>
        [Fact]
        public void ARawIterationAfterAStagedRowLandsAfterIt()
        {
            var g = new ContextGraph();
            var f = ValueFunction(g, MergeFn.New, "f");
            var a = g.Root.FreshId();
            var action = new TableAction(g.EGraph, f);
            var database = g.EGraph.Database;

            g.Root.StageRow(f, [a, g.Int(1)]);
            g.EGraph.RunIteration(ts =>
            {
                database.WithExecutionState(state =>
                {
                    action.Insert(state, [a.Rep, g.Int(2).Rep]);
                    return 0;
                }, ts);
                return new RuleSetReport(database.MergeAll(ts), new(), TimeSpan.Zero, TimeSpan.Zero);
            });
            g.EGraph.FlushUpdates();

            Assert.Equal(g.Int(2), g.Root.LookupId(f, [a]));
        }

    }

}

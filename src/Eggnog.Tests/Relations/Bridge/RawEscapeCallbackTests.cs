using System;

using Xunit;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// The raw escape's action (<c>WithExecutionState</c>, egglog's <c>with_execution_state</c>) runs as a callback of
    /// the context's thread domain, as egglog's closure borrows the e-graph shared: reads and a nested raw escape are
    /// allowed inside it, and every call that writes or changes a context of the domain is refused there without
    /// staging anything. A frozen root refuses the raw escape as it refuses <see cref="EGraph.AddValues"/>. The root's
    /// raw iteration (<see cref="EGraph.RunIteration"/>) is a raw write path too: it flushes what the context API
    /// staged first, so writes land in the order the host issued them.
    /// </summary>
    public class RawEscapeCallbackTests
    {

        /// <summary>
        /// Inside the e-graph's raw escape, every write and run on the root is refused (the egglog-compatible calls and
        /// the context API alike), reads and a nested raw escape work, and afterwards only the action's own rows land:
        /// the refused calls staged nothing and moved no clock.
        /// </summary>
        [Fact]
        public void InsideTheRootsRawEscapeWritesAndRunsAreRefused()
        {
            var g = new ContextGraph();
            var a = g.Root.FreshId();
            var b = g.Root.FreshId();
            var c = g.Root.FreshId();
            var rule = g.EdgeToPath();
            g.EGraph.AddValues([(g.Edge, [c, a, g.Unit])]);
            var edge = new TableAction(g.EGraph, g.Edge);
            var before = g.EGraph.NextTimestamp;

            g.EGraph.WithExecutionState(state =>
            {
                edge.Insert(state, [a.Rep, b.Rep, g.Unit.Rep]);
                Assert.Throws<InvalidOperationException>(() => g.EGraph.RunRules([rule]));
                Assert.Throws<InvalidOperationException>(() => g.EGraph.FlushUpdates());
                Assert.Throws<InvalidOperationException>(() => g.EGraph.AddValues([(g.Edge, [b, a, g.Unit])]));
                Assert.Throws<InvalidOperationException>(() => g.EGraph.AddTerm(g.Num, [g.Int(1)]));
                Assert.Throws<InvalidOperationException>(() => g.EGraph.ClearTable(g.Path));
                Assert.Throws<InvalidOperationException>(() => g.EGraph.RunIteration(ts => new RuleSetReport(false, new(), TimeSpan.Zero, TimeSpan.Zero)));
                Assert.Throws<InvalidOperationException>(() => g.Root.RunRules([rule]));
                Assert.Throws<InvalidOperationException>(() => g.Root.FlushUpdates());
                Assert.Throws<InvalidOperationException>(() => g.Root.StageRow(g.Edge, [b, c, g.Unit]));
                Assert.Throws<InvalidOperationException>(() => g.Root.StageTerm(g.Num, [g.Int(2)]));
                Assert.Throws<InvalidOperationException>(() => g.Root.StageUnion(a, b));
                Assert.Throws<InvalidOperationException>(() => g.Root.AddValues([(g.Edge, [b, c, g.Unit])]));
                Assert.Throws<InvalidOperationException>(() => g.Root.AddTerm(g.Num, [g.Int(3)]));

                // Reads, a fresh id and a nested raw escape are allowed.
                Assert.Single(ContextGraph.Keys(g.Root, g.Edge));
                Assert.NotEqual(a, g.Root.FreshId());
                g.EGraph.WithExecutionState(inner =>
                {
                    edge.Insert(inner, [b.Rep, c.Rep, g.Unit.Rep]);
                    return 0;
                });
                return 0;
            });

            Assert.Equal(before, g.EGraph.NextTimestamp);
            Assert.True(g.Root.IsClean);
            g.EGraph.FlushUpdates();
            Assert.Equal(Sorted([$"{a.Rep} {b.Rep}", $"{b.Rep} {c.Rep}", $"{c.Rep} {a.Rep}"]), ContextGraph.Keys(g.Root, g.Edge));
            Assert.Null(g.EGraph.LookupId(g.Num, [g.Int(1)]));
            Assert.Null(g.EGraph.LookupId(g.Num, [g.Int(2)]));
            Assert.Null(g.EGraph.LookupId(g.Num, [g.Int(3)]));

            // The action's rows are matched by the next runs: nothing was hidden under a watermark.
            for (var i = 0; i < 3; i++)
                g.EGraph.RunRules([rule]);

            Assert.Equal(3, ContextGraph.Keys(g.Root, g.Path).Length);
            g.Root.CheckInvariants();
        }

        /// <summary>
        /// Inside a child's raw escape, the child's writes and runs are refused, and the child stays writable and
        /// consistent: the action's row lands at the next flush and a run afterwards matches it.
        /// </summary>
        [Fact]
        public void InsideAChildsRawEscapeWritesAndRunsAreRefused()
        {
            var g = new ContextGraph();
            var a = g.Root.FreshId();
            var b = g.Root.FreshId();
            var rule = g.EdgeToPath();
            g.Root.Freeze();

            using var child = g.Root.CreateChild();
            var low = new TableAction(g.EGraph, g.Low);
            child.WithExecutionState(state =>
            {
                low.Insert(state, [a.Rep, g.Int(4).Rep]);
                Assert.Throws<InvalidOperationException>(() => child.RunRules([rule]));
                Assert.Throws<InvalidOperationException>(() => child.FlushUpdates());
                Assert.Throws<InvalidOperationException>(() => child.StageRow(g.Edge, [a, b, g.Unit]));
                Assert.Throws<InvalidOperationException>(() => child.StageUnion(a, b));
                Assert.Throws<InvalidOperationException>(() => child.AddValues([(g.Low, [a, g.Int(1)])]));
                return 0;
            });

            Assert.False(child.IsFaulted);
            child.FlushUpdates();
            Assert.Equal(g.Int(4), child.LookupId(g.Low, [a]));
            Assert.Empty(ContextGraph.Keys(child, g.Edge));
            child.StageRow(g.Edge, [a, b, g.Unit]);
            child.RunRules([rule]);
            child.RunRules([rule]);
            Assert.Equal([$"{a.Rep} {b.Rep}"], ContextGraph.Keys(child, g.Path));
            child.CheckInvariants();
        }

        /// <summary>
        /// The root's raw iteration with nothing staged through the context API ticks the clock once, as egglog's
        /// <c>run_rules_inner</c> does: flushing staged writes first costs one emptiness test.
        /// </summary>
        [Fact]
        public void TheRawIterationAloneTicksTheClockOnce()
        {
            var g = new ContextGraph();
            var before = g.EGraph.NextTimestamp;
            uint seen = 0;

            g.EGraph.RunIteration(ts =>
            {
                seen = ts;
                return new RuleSetReport(false, new(), TimeSpan.Zero, TimeSpan.Zero);
            });

            Assert.Equal(before, seen);
            Assert.Equal(before + 1, g.EGraph.NextTimestamp);
        }

        /// <summary>
        /// A row staged at the root through the context API is merged before the raw iteration's epoch, in an epoch of
        /// its own, so the iteration's rule set sees it as already present.
        /// </summary>
        [Fact]
        public void TheRawIterationFlushesTheRowsStagedBeforeIt()
        {
            var g = new ContextGraph();
            var a = g.Root.FreshId();
            var b = g.Root.FreshId();
            g.Assert(g.Root, g.Edge, a, b);
            var before = g.EGraph.NextTimestamp;
            var rows = -1;

            g.EGraph.RunIteration(ts =>
            {
                rows = g.EGraph.TableSize(g.Edge);
                return new RuleSetReport(false, new(), TimeSpan.Zero, TimeSpan.Zero);
            });

            Assert.Equal(1, rows);
            Assert.Equal(before + 2, g.EGraph.NextTimestamp);
            Assert.True(g.Root.IsClean);
            g.Root.CheckInvariants();
        }

        /// <summary>
        /// Sorts strings ordinally.
        /// </summary>
        static string[] Sorted(string[] values)
        {
            Array.Sort(values, StringComparer.Ordinal);
            return values;
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
        /// The e-graph's raw escape on a frozen root: the egglog-compatible <see cref="EGraph.AddValues"/> refuses a
        /// frozen root, and so does the raw escape, rather than stage rows into the frozen database that no flush can
        /// ever merge (the root's flush refuses a frozen context).
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
        /// A row staged through the context API from inside the raw escape's action, before the action's own raw row
        /// for the same key. The stage is refused, as the context's storage-level escape (<c>Context.Write</c>) refuses
        /// changes from inside its action; an accepted stage would have to land in the order the host issued it, so
        /// that under <c>:merge new</c> the raw row, issued last, wins. Merging the earlier write last is never right.
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
        /// A rule run from inside the raw escape's action is refused, as <c>Context.Write</c> refuses changes from its
        /// action. The action's row is stamped with the clock read before the run, so once such a run committed its
        /// watermark the row would lie below it when it merges, and no later run would match it; an accepted run would
        /// have to leave the row matched.
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
        /// A row staged at the root through the context API, then a raw root iteration
        /// (<see cref="EGraph.RunIteration"/>, under <see cref="EGraph.RunRuleSet"/>) whose rule set writes the same
        /// key, then a flush: the writes land in the order the host issued them, so under <c>:merge new</c> the
        /// iteration's row wins.
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

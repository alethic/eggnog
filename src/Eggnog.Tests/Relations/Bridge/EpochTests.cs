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
    /// Timestamps of a store-backed e-graph's root: a program that creates no child gets egglog's numbering exactly,
    /// and writes staged at the root merge inside the next run at its timestamp, also when the run's rule set is empty
    /// or a child advanced the clock in between.
    /// </summary>
    public class EpochTests
    {

        /// <summary>
        /// The clock after each root operation, and the stamp of the rows each writes, follow egglog-bridge's rules
        /// (lib.rs:583-682, 946-959): a run or flush reads the clock, writes at it and advances it once; a run that
        /// grew the union-find leaves the advance to the rebuild, whose first pass reuses the run's timestamp; every
        /// pass advances the clock, the last one (which changes nothing) included. The context API's run adds nothing
        /// at rest (<c>EnsureClean</c>).
        /// </summary>
        [Fact]
        public void RootTimestampSequenceMatchesEgglog()
        {
            var g = new ContextGraph();
            var egraph = g.EGraph;
            var edgeToPath = g.EdgeToPath();
            var unionEdges = g.UnionEdges();
            var a = egraph.FreshId();
            var b = egraph.FreshId();
            Assert.Equal(1u, egraph.NextTimestamp);

            // add_values: merged at 1, then the clock advances.
            egraph.AddValues([(g.Edge, [a, b, g.Unit])]);
            Assert.Equal(2u, egraph.NextTimestamp);
            Assert.Equal(1u, Stamp(egraph, g.Edge, a, b));

            // add_term of a new term: merged at 2.
            var x = egraph.AddTerm(g.Num, [g.Int(1)]);
            Assert.Equal(3u, egraph.NextTimestamp);
            Assert.Equal(2u, Stamp(egraph, g.Num, g.Int(1)));

            // add_term of an existing term: the union-id merge unions the fresh id at 3, the clock advances to 4, and
            // the rebuild runs one pass at 4 that changes nothing.
            Assert.Equal(x, egraph.AddTerm(g.Num, [g.Int(1)]));
            Assert.Equal(5u, egraph.NextTimestamp);
            Assert.Equal(2u, Stamp(egraph, g.Num, g.Int(1)));

            // A run without unions: rows at 5, the clock at 6.
            var report = egraph.RunRules(edgeToPath);
            Assert.True(report.Changed);
            Assert.Equal(0, report.RebuildPasses);
            Assert.Equal(6u, egraph.NextTimestamp);
            Assert.Equal(5u, Stamp(egraph, g.Path, a, b));

            // A run that unions: its rebuild's first pass reuses 6 and changes the rows, the second pass at 7 changes
            // nothing.
            report = egraph.RunRules(unionEdges);
            Assert.True(report.Changed);
            Assert.True(report.RebuildChanged);
            Assert.Equal(2, report.RebuildPasses);
            Assert.False(report.Stopped);
            Assert.Equal(8u, egraph.NextTimestamp);
            var c = egraph.GetCanonInUf(b);
            Assert.Equal(a, c);
            Assert.Equal(6u, Stamp(egraph, g.Edge, c, c));
            Assert.Equal(6u, Stamp(egraph, g.Path, c, c));

            // An empty flush and an empty run are epochs too.
            Assert.False(egraph.FlushUpdates());
            Assert.Equal(9u, egraph.NextTimestamp);
            Assert.False(egraph.RunRules().Changed);
            Assert.Equal(10u, egraph.NextTimestamp);

            // The context API at the root: a flush at 10, and a run that adds no rebuild and no epoch at rest.
            g.Assert(g.Root, g.Mark, a);
            Assert.True(g.Root.FlushUpdates());
            Assert.Equal(11u, egraph.NextTimestamp);
            Assert.Equal(10u, Stamp(egraph, g.Mark, a));
            g.Root.RunRules(edgeToPath);
            Assert.Equal(12u, egraph.NextTimestamp);
        }

        /// <summary>
        /// A row staged at the root through <see cref="EGraph.WithExecutionState{TResult}"/> and not flushed merges in
        /// the next run at its timestamp, as in egglog's <c>run_rule_set</c>; a lookup between the staging and the run
        /// does not throw and sees the committed rows only, through either API.
        /// </summary>
        [Fact]
        public void RootStagedWritesMergeInRun()
        {
            var g = new ContextGraph();
            var egraph = g.EGraph;
            var rule = g.EdgeToPath();
            var a = egraph.FreshId();
            var b = egraph.FreshId();

            // mark(x) => path(x, x): a rule with rows to match, so the run's rule set is not empty (an empty one merges
            // nothing, as egglog's run_rule_set returns before its merge_all).
            var rb = egraph.NewRule("mark-path", true);
            var x = rb.NewVar(ColumnTy.Id);
            rb.QueryTable(g.Mark, [x, rb.NewVar(g.UnitType)], false);
            rb.Set(g.Path, [x, x, RuleEntry.Const(g.Unit, g.UnitType)]);
            var marks = rb.Build();
            egraph.AddValues([(g.Mark, [a, g.Unit])]);
            var edge = new TableAction(egraph, g.Edge);
            egraph.WithExecutionState(state =>
            {
                edge.Insert(state, [a.Rep, b.Rep, g.Unit.Rep]);
                return 0;
            });

            Assert.Null(egraph.LookupId(g.Edge, [a, b]));
            Assert.True(g.Root.IsClean);
            Assert.Null(g.Root.LookupId(g.Edge, [a, b]));
            Assert.Equal(2u, egraph.NextTimestamp);

            // The edge rule matches nothing (the row is not merged when it searches); the run merges the staged row at
            // 2.
            var report = g.Root.RunRules(marks, rule);
            Assert.Equal(1, report.RuleSetReport.NumMatches("mark-path"));
            Assert.Equal(0, report.RuleSetReport.NumMatches("edge-path"));
            Assert.True(report.Changed);
            Assert.Equal(3u, egraph.NextTimestamp);
            Assert.Equal(g.Unit, g.Root.LookupId(g.Edge, [a, b]));
            Assert.Equal(2u, Stamp(egraph, g.Edge, a, b));

            // The row carries the run's timestamp, the rule's watermark: it is new to the rule's next run.
            report = g.Root.RunRules(rule);
            Assert.Equal(1, report.RuleSetReport.NumMatches("edge-path"));
            Assert.Equal(["0 0", "0 1"], ContextGraph.Keys(g.Root, g.Path));
        }

        /// <summary>
        /// A root rule run notes its timestamp as the root's semi-naive floor (<see cref="Context.NoteRuleRun"/>), and
        /// a child's runs refuse nothing they should accept: every run of a context captures a nondecreasing epoch,
        /// even while a sibling under a frozen parent advances the clock.
        /// </summary>
        [Fact]
        public void RunsNoteTheirEpochAsTheFloor()
        {
            var g = new ContextGraph();
            var rule = g.EdgeToPath();
            var t = g.EGraph.NextTimestamp;
            g.Root.RunRules(rule);
            Assert.Equal(t, g.Root.Context.SemiNaiveFloor);

            g.Root.Freeze();
            using var left = g.Root.CreateChild();
            using var right = g.Root.CreateChild();
            g.Assert(left, g.Edge, left.FreshId(), left.FreshId());
            left.FlushUpdates();
            var before = g.EGraph.NextTimestamp;
            right.RunRules(rule);
            left.RunRules(rule);
            Assert.True(left.Context.SemiNaiveFloor > before);
            Assert.True(right.Context.SemiNaiveFloor >= before);
            left.CheckInvariants();
            right.CheckInvariants();
        }

        /// <summary>
        /// The timestamp cell of a function's row at the root.
        /// </summary>
        static uint Stamp(EGraph egraph, FunctionId function, params Value[] key)
        {
            var info = egraph.GetFunctionInfo(function);
            var keys = new uint[key.Length];
            for (var i = 0; i < key.Length; i++)
                keys[i] = key[i].Rep;

            return PhysicalRow(egraph, function, keys)[info.Math.TimestampColumn];
        }

        /// <summary>
        /// Through the e-graph's egglog-compatible <see cref="EGraph.RunRules"/>, a row staged raw at the root before
        /// runs whose rule sets are empty lands at a run's timestamp, so a later run matches it. egglog's
        /// <c>run_rule_set</c> returns before <c>merge_all</c> when the rule set is empty
        /// (core-relations/src/free_join/execute.rs:349); the root merges what is pending anyway.
        /// </summary>
        [Fact]
        public void TheEgglogRunMergesARowStagedRawBeforeAnEmptyRun()
        {
            var g = new ContextGraph();
            var rule = g.EdgeToPath();
            var a = g.Root.FreshId();
            var b = g.Root.FreshId();
            var c = g.Root.FreshId();
            g.EGraph.AddValues([(g.Edge, [b, c, g.Unit])]);
            g.EGraph.RunRules(rule);

            var edge = new TableAction(g.EGraph, g.Edge);
            g.EGraph.WithExecutionState(state =>
            {
                edge.Insert(state, [a.Rep, b.Rep, g.Unit.Rep]);
                return 0;
            });

            var empty = g.EGraph.RunRules(rule);
            Assert.True(empty.Changed);
            g.EGraph.RunRules(rule);
            g.EGraph.RunRules(rule);
            Assert.Equal(g.Unit, g.Root.LookupId(g.Path, [a, b]));
        }

        /// <summary>
        /// A row staged at the root through <see cref="EGraphContext.WithExecutionState{TResult}"/> stays pending and
        /// merges inside the next root run. A child of the writable root advances the shared clock between the staging
        /// and the run, so the run's timestamp is larger than the row's stamp; the row must still be new to a later
        /// run, or every match over it would be lost at the root. Without the child the same program derives it
        /// (<see cref="RootStagedWritesMergeInRun"/>).
        /// </summary>
        [Fact]
        public void ARootRowStagedRawIsMatchedAfterAChildAdvancedTheClock()
        {
            var g = new ContextGraph();
            var rule = g.EdgeToPath();
            var a = g.Root.FreshId();
            var b = g.Root.FreshId();
            using var child = g.Root.CreateChild();
            var edge = new TableAction(g.EGraph, g.Edge);
            g.Root.WithExecutionState(state =>
            {
                edge.Insert(state, [a.Rep, b.Rep, g.Unit.Rep]);
                return 0;
            });

            var staged = g.EGraph.NextTimestamp;
            child.FlushUpdates();
            Assert.True(g.EGraph.NextTimestamp > staged);

            // The first run merges the staged edge after its search; the second must see it as new.
            g.Root.RunRules(rule);
            Assert.Equal(g.Unit, g.Root.LookupId(g.Edge, [a, b]));
            g.Root.RunRules(rule);
            g.Root.RunRules(rule);
            Assert.Equal(g.Unit, g.Root.LookupId(g.Path, [a, b]));
        }

        /// <summary>
        /// A row staged raw at the root stays pending into the next run and merges there at the run's timestamp, so
        /// that it is new to the rule's next run. Here the rule already ran, so its semi-naive variants find no row at
        /// or after its watermark and the rule set is empty. egglog's <c>run_rule_set</c> merges nothing for an empty
        /// rule set while the run still commits its timestamp as the rule's watermark and advances the clock; a row
        /// merged later with its staging stamp would lie below the watermark, and no run would ever match it.
        /// </summary>
        [Fact]
        public void ARootRowStagedRawBeforeAnEmptyRunIsMatchedLater()
        {
            var g = new ContextGraph();
            var rule = g.EdgeToPath();
            var a = g.Root.FreshId();
            var b = g.Root.FreshId();
            var c = g.Root.FreshId();
            g.Root.AddValues([(g.Edge, [b, c, g.Unit])]);
            g.Root.RunRules(rule);
            Assert.Equal(g.Unit, g.Root.LookupId(g.Path, [b, c]));

            var edge = new TableAction(g.EGraph, g.Edge);
            g.Root.WithExecutionState(state =>
            {
                edge.Insert(state, [a.Rep, b.Rep, g.Unit.Rep]);
                return 0;
            });

            // Runs until the staged edge is merged, then once more: the edge must have been matched by then.
            for (var i = 0; i < 4 && g.Root.LookupId(g.Edge, [a, b]) is null; i++)
                g.Root.RunRules(rule);

            // The runs found no new edge, so each built an empty rule set; such a run merges what is pending at its own
            // timestamp, so the edge lies at or above the watermark, and the flush finds nothing left to merge.
            g.Root.FlushUpdates();
            Assert.Equal(g.Unit, g.Root.LookupId(g.Edge, [a, b]));
            g.Root.RunRules(rule);
            g.Root.RunRules(rule);
            Assert.Equal(g.Unit, g.Root.LookupId(g.Path, [a, b]));
        }

        /// <summary>
        /// The same through inheritance: after two root runs with nothing new and a freeze, a child of the frozen root
        /// seeds its watermark from the root's, and it still matches the row staged raw before those runs, since the
        /// row merged at a run's timestamp.
        /// </summary>
        [Fact]
        public void AChildOfAFrozenRootMatchesARowStagedRawBeforeAnEmptyRootRun()
        {
            var g = new ContextGraph();
            var rule = g.EdgeToPath();
            var a = g.Root.FreshId();
            var b = g.Root.FreshId();
            var c = g.Root.FreshId();
            g.Root.AddValues([(g.Edge, [b, c, g.Unit])]);
            g.Root.RunRules(rule);

            var edge = new TableAction(g.EGraph, g.Edge);
            g.Root.WithExecutionState(state =>
            {
                edge.Insert(state, [a.Rep, b.Rep, g.Unit.Rep]);
                return 0;
            });

            // Two runs with nothing new: the second commits a watermark above the staged row's stamp.
            g.Root.RunRules(rule);
            g.Root.RunRules(rule);
            g.Root.Freeze();
            Assert.Equal(g.Unit, g.Root.LookupId(g.Edge, [a, b]));

            using var child = g.Root.CreateChild();
            child.Saturate([rule], 4);
            Assert.Equal(g.Unit, child.LookupId(g.Path, [a, b]));
        }

    }

}

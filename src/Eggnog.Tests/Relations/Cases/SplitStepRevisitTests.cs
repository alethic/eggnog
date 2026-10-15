using System;
using System.Linq;

using Xunit;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// A later split step in a scope that already holds a live record. A step runs and lifts every record of its
    /// driver, and the rule layer relies on it: the driver keeps a record until the run ends, the cases take in the
    /// parent's new facts through their inherited watermarks, and a case opened for a value the facts later exclude
    /// goes vacuous and is dropped. So a step whose requests are all covered by live records still brings those
    /// records' cases up to date, runs them and lifts them again (the lift memo, keyed on the parent's version, makes
    /// that free when nothing changed), and a step that finds nothing new reports no change.
    /// </summary>
    public class SplitStepRevisitTests
    {

        /// <summary>
        /// The boolean split of <c>b</c> over <c>{t, f}</c> is opened before the scope knows anything about <c>t</c> or
        /// <c>f</c>. Afterwards the scope learns <c>edge(t, c)</c> and <c>edge(f, c)</c> (a rewrite further on in the
        /// schedule, or a host write). The next split step finds the key covered, but both live cases now hold
        /// <c>edge(b, c)</c> and, after running the in-case schedule, <c>path(b, c)</c>: the step lifts them.
        /// </summary>
        [Fact]
        public void ALaterStepLiftsWhatExistingCasesDeriveFromNewScopeFacts()
        {
            var g = new ContextGraph();
            g.EGraph.DesignateContradiction(g.Bottom);
            var copy = g.EdgeToPath();
            var (t, f, c, b) = (g.Root.FreshId(), g.Root.FreshId(), g.Root.FreshId(), g.Root.FreshId());
            g.Root.FlushUpdates();

            var source = new TestSplitSource(0, _ => [([b.Rep], 2)], (@case, i, _) => @case.StageUnion(b, i == 0 ? t : f));
            var schedule = new RecordingSchedule((@case, _, _) => @case.Saturate([copy], 8).Iterations > 1);
            var spec = new LiftSpec(
                new LiftTable(g.Edge, LiftTableKind.Relation),
                new LiftTable(g.Path, LiftTableKind.Relation),
                new LiftTable(g.Mark, LiftTableKind.Relation));
            var driver = new SplitDriver([source], schedule, null, spec, SplitOptions.Default);

            try
            {
                Assert.True(driver.Step(g.Root, 0, new SplitReport()));
                Assert.False(g.Root.ContainsKey(g.Edge, [b, c]));

                // The scope learns new facts after the split was opened.
                g.Assert(g.Root, g.Edge, t, c);
                g.Assert(g.Root, g.Edge, f, c);
                g.Root.FlushUpdates();

                var report = new SplitReport();
                var changed = driver.Step(g.Root, 0, report);
                Assert.True(g.Root.ContainsKey(g.Edge, [b, c]), "edge(b, c) holds in both live cases of the scope's live record, but the later split step did not lift it");
                Assert.True(g.Root.ContainsKey(g.Path, [b, c]), "path(b, c) follows in both live cases, but the later split step did not lift it");
                Assert.True(changed);
            }
            finally
            {
                SplitDriver.DisposeRecords(g.Root);
            }
        }

        /// <summary>
        /// A case whose value the scope's new facts exclude goes vacuous and is dropped, and what the remaining live
        /// case holds is lifted. The split of <c>b</c> over <c>{t, f}</c> is opened first; then the scope learns
        /// <c>edge(b, f)</c>, which in the case <c>b = f</c> is a self-loop, and the in-case rule <c>edge(x, x) =&gt;
        /// bottom()</c> makes that case vacuous. The next split step drops it and lifts <c>b = t</c> from the one live
        /// case.
        /// </summary>
        [Fact]
        public void ALaterStepDropsACaseTheScopesNewFactsExcludeAndLiftsTheRest()
        {
            var g = new ContextGraph();
            g.EGraph.DesignateContradiction(g.Bottom);
            var loop = SelfLoopIsBottom(g);
            var (t, f, b) = (g.Root.FreshId(), g.Root.FreshId(), g.Root.FreshId());
            g.Root.FlushUpdates();

            var source = new TestSplitSource(0, _ => [([b.Rep], 2)], (@case, i, _) => @case.StageUnion(b, i == 0 ? t : f));
            var schedule = new RecordingSchedule((@case, _, _) => @case.Saturate([loop], 8).Iterations > 1);
            var spec = new LiftSpec(new LiftTable(g.Edge, LiftTableKind.Relation));
            var driver = new SplitDriver([source], schedule, null, spec, SplitOptions.Default);

            try
            {
                Assert.True(driver.Step(g.Root, 0, new SplitReport()));
                Assert.NotEqual(g.Root.Find(b), g.Root.Find(t));

                g.Assert(g.Root, g.Edge, b, f);
                g.Root.FlushUpdates();
                Assert.False(g.Root.IsContradictory);

                driver.Step(g.Root, 0, new SplitReport());
                Assert.True(g.Root.Find(b) == g.Root.Find(t), "the case b = f is vacuous once it sees edge(b, f), so b = t holds in every live case, but the later split step did not lift it");
            }
            finally
            {
                SplitDriver.DisposeRecords(g.Root);
            }
        }

        /// <summary>
        /// <c>edge(x, x) =&gt; bottom()</c>.
        /// </summary>
        static EGraphRuleId SelfLoopIsBottom(ContextGraph g)
        {
            var rb = g.EGraph.NewRule("self-loop-bottom", true);
            var x = rb.NewVar(ColumnTy.Id);
            rb.QueryTable(g.Edge, [x, x, rb.NewVar(g.UnitType)], false);
            rb.Set(g.Bottom, [RuleEntry.Const(g.Unit, g.UnitType)]);
            return rb.Build();
        }

        static LiftSpec MarkSpec(ContextGraph g) => new(new LiftTable(g.Mark, LiftTableKind.Relation));

        /// <summary>
        /// A split step revisits the scope's live records, and a step that finds nothing new reports no change, so a
        /// schedule that repeats the step until nothing changes ends; the memo keeps the revisit's lift free.
        /// </summary>
        [Fact]
        public void ARevisitThatFindsNothingNewReportsNoChange()
        {
            var g = new ContextGraph();
            g.EGraph.DesignateContradiction(g.Bottom);
            var (t, f, b) = (g.Root.FreshId(), g.Root.FreshId(), g.Root.FreshId());
            g.Root.FlushUpdates();
            var source = new TestSplitSource(0, _ => [([b.Rep], 2)], (@case, i, _) =>
            {
                @case.StageUnion(b, i == 0 ? t : f);
                g.Assert(@case, g.Mark, t);
            });

            var schedule = new RecordingSchedule();
            var driver = new SplitDriver([source], schedule, null, MarkSpec(g), SplitOptions.Default);
            try
            {
                Assert.True(driver.Step(g.Root, 0, new SplitReport()));
                Assert.True(g.Root.ContainsKey(g.Mark, [t]));
                var calls = schedule.Calls.Count;

                var report = new SplitReport();
                Assert.False(driver.Step(g.Root, 0, report));
                Assert.Equal(0, report.CasesOpened);
                Assert.Equal(1, report.SplitsSkipped);
                Assert.True(report.Lifts >= 1, "the live record is lifted again");
                Assert.True(schedule.Calls.Count > calls, "the live record's cases run again");
                Assert.False(driver.Step(g.Root, 0, new SplitReport()));
            }
            finally
            {
                SplitDriver.DisposeRecords(g.Root);
            }
        }

        /// <summary>
        /// A revisit that finds nothing new is answered by the lift memo: the step's first lift of the new record runs
        /// in full, and every lift of the revisit, whose cases run again and learn nothing, is a memo answer, counted in
        /// <see cref="SplitReport.LiftsMemoized"/> among <see cref="SplitReport.Lifts"/>.
        /// </summary>
        [Fact]
        public void ARevisitWithNothingNewIsAnsweredByTheMemo()
        {
            var g = new ContextGraph();
            g.EGraph.DesignateContradiction(g.Bottom);
            var (t, f, b) = (g.Root.FreshId(), g.Root.FreshId(), g.Root.FreshId());
            g.Root.FlushUpdates();
            var source = new TestSplitSource(0, _ => [([b.Rep], 2)], (@case, i, _) =>
            {
                @case.StageUnion(b, i == 0 ? t : f);
                g.Assert(@case, g.Mark, t);
            });

            var driver = new SplitDriver([source], new RecordingSchedule(), null, MarkSpec(g), SplitOptions.Default);
            try
            {
                var first = new SplitReport();
                Assert.True(driver.Step(g.Root, 0, first));
                Assert.True(first.Lifts >= 1);
                Assert.True(first.LiftsMemoized < first.Lifts, $"the new record's first lift runs in full ({first})");

                var revisit = new SplitReport();
                Assert.False(driver.Step(g.Root, 0, revisit));
                Assert.True(revisit.Lifts >= 1, "the live record is lifted again");
                Assert.Equal(revisit.Lifts, revisit.LiftsMemoized);
            }
            finally
            {
                SplitDriver.DisposeRecords(g.Root);
            }
        }

    }

}

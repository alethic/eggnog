using System;

using Xunit;

using static Eggnog.Relations.Tests.BridgeTestUtil;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// Tests for the iteration and rebuild loop of an <see cref="EGraph"/> (egglog-bridge/src/lib.rs:587-682):
    /// <see cref="EGraph.RunIteration"/> and <see cref="EGraph.RunRuleSet"/> with hand-built core rules standing in for
    /// the rule layer, which lowers egglog rules onto the same core rules.
    /// </summary>
    public class EGraphRunTests
    {

        [Fact]
        public void AnIterationWithoutUnionsAdvancesTheTimestampOnce()
        {
            var egraph = NewEGraph();
            uint seen = 0;
            var report = egraph.RunIteration(ts =>
            {
                seen = ts;
                return RuleSetReport.Empty;
            });
            Assert.Equal(1u, seen);
            Assert.Equal(2u, egraph.NextTimestamp);
            Assert.False(report.Changed);
            Assert.False(report.RebuildChanged);
            Assert.Equal(TimeSpan.Zero, report.RebuildTime);
            Assert.Same(RuleSetReport.Empty, report.RuleSetReport);
        }

        [Fact]
        public void UnionsInARunTriggerTheRebuildLoop()
        {
            var fx = new RunFixture();
            var egraph = fx.EGraph;
            var n1 = fx.Number(1);
            var n2 = fx.Number(2);
            var s1 = egraph.AddTerm(fx.Add, [n1, n1]);
            var s2 = egraph.AddTerm(fx.Add, [n2, n2]);
            egraph.AddTerm(fx.Same, [n1, n2]);
            Assert.NotEqual(fx.Canon(s1), fx.Canon(s2));

            var start = egraph.NextTimestamp;
            using var rules = fx.UnionRule();
            var report = egraph.RunRuleSet(rules);

            Assert.True(report.Changed);
            Assert.True(report.RebuildChanged);
            Assert.Equal(fx.Canon(n1), fx.Canon(n2));
            Assert.Equal(fx.Canon(s1), fx.Canon(s2));

            // The tables are canonical: add holds one row, over the surviving ids.
            var survivor = fx.Canon(n1);
            Assert.Equal([$"{survivor.Rep} {survivor.Rep} {fx.Canon(s1).Rep}"], Show(egraph, fx.Add));

            // The first rebuild pass reuses the run's timestamp (lib.rs:667), and every pass advances it.
            Assert.Equal(start, PhysicalRow(egraph, fx.Same, survivor.Rep, survivor.Rep)[3]);
            Assert.True(egraph.NextTimestamp >= start + 2);
        }

        [Fact]
        public void RunsStampTheirRowsAndSaturate()
        {
            var fx = new RunFixture();
            var egraph = fx.EGraph;
            var n1 = fx.Number(1);
            var n2 = fx.Number(2);
            var s = egraph.AddTerm(fx.Add, [n1, n2]);
            var t = egraph.AddTerm(fx.Add, [n2, n1]);
            Assert.NotEqual(s, t);

            var start = egraph.NextTimestamp;
            IterationReport report;
            using (var rules = fx.CommRule())
                report = egraph.RunRuleSet(rules);

            Assert.True(report.Changed);
            Assert.Equal(fx.Canon(s), fx.Canon(t));
            Assert.Equal(start, PhysicalRow(egraph, fx.Add, n2.Rep, n1.Rep)[3]);

            // Re-deriving the same facts changes nothing, and the timestamp moves by one.
            var before = egraph.NextTimestamp;
            using (var rules = fx.CommRule())
                report = egraph.RunRuleSet(rules);

            Assert.False(report.Changed);
            Assert.False(report.RebuildChanged);
            Assert.Equal(before + 1, egraph.NextTimestamp);
            Assert.Equal(2, egraph.TableSize(fx.Add));
        }

        [Fact]
        public void APanicInARunIsThrownAfterTheMergeAndTheRebuild()
        {
            var fx = new RunFixture();
            var egraph = fx.EGraph;
            var n1 = fx.Number(1);
            var n2 = fx.Number(2);
            var s1 = egraph.AddTerm(fx.Add, [n1, n1]);
            var s2 = egraph.AddTerm(fx.Add, [n2, n2]);
            egraph.AddTerm(fx.Same, [n1, n2]);
            var panic = egraph.NewPanic("rule panicked");

            var start = egraph.NextTimestamp;
            var error = Assert.Throws<PanicException>(() =>
            {
                using var rules = fx.UnionRule(panic);
                egraph.RunRuleSet(rules);
            });
            Assert.Equal("rule panicked", error.PanicMessage);

            // The union staged before the panic merged, and the e-graph is rebuilt and moved on (egglog leaves it
            // non-canonical and reuses the timestamp).
            Assert.Equal(fx.Canon(n1), fx.Canon(n2));
            Assert.Equal(fx.Canon(s1), fx.Canon(s2));
            Assert.Single(Show(egraph, fx.Add));
            Assert.True(egraph.NextTimestamp > start);
            Assert.False(egraph.TryTakePanic(out _));

            // The next iteration is clean.
            var report = egraph.RunIteration(_ => RuleSetReport.Empty);
            Assert.False(report.Changed);
        }

        [Fact]
        public void APanicInARunWithoutUnionsStillAdvancesTheTimestamp()
        {
            var egraph = NewEGraph();
            var panic = egraph.NewPanic("p");
            Assert.Throws<PanicException>(() => egraph.RunIteration(ts =>
            {
                egraph.Database.WithExecutionState(state => state.TryCallExternalFunction(panic, ReadOnlySpan<uint>.Empty, out _), ts);
                return RuleSetReport.Empty;
            }));
            Assert.Equal(2u, egraph.NextTimestamp);
        }

        [Fact]
        public void AnEGraphWithoutFunctionsRebuildsItsUnionFind()
        {
            var egraph = NewEGraph();
            var a = egraph.FreshId();
            var b = egraph.FreshId();
            var report = egraph.RunIteration(ts =>
            {
                egraph.Database.WithExecutionState(state =>
                {
                    egraph.ActionRegistry.UnionAction.Union(state, a.Rep, b.Rep);
                    return 0;
                }, ts);
                return new RuleSetReport(egraph.Database.MergeAll(ts), new(), TimeSpan.Zero, TimeSpan.Zero);
            });
            Assert.True(report.Changed);
            Assert.False(report.RebuildChanged);
            Assert.Equal(a, egraph.GetCanonRepr(b, ColumnTy.Id));
        }

        [Fact]
        public void RunIterationRejectsAMissingReport()
        {
            var egraph = NewEGraph();
            Assert.Throws<InvalidOperationException>(() => egraph.RunIteration(_ => null!));
            Assert.Throws<ArgumentNullException>(() => egraph.RunIteration(null!));
            Assert.Throws<ArgumentNullException>(() => egraph.RunRuleSet(null!));
        }

    }

}

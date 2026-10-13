using System;
using System.Linq;

using Xunit;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// Contradiction in the bridge (colored-contexts spec §6.9, §10.6, §17 M10.3; rule-layer E4, D11): designating the
    /// contradiction function, the early stop of a case's search, the stop flag a run reports, and contradiction as an
    /// ordinary fact elsewhere. Named apart from the storage <c>ContradictionTests</c>.
    /// </summary>
    public class BridgeContradictionTests
    {

        [Fact]
        public void DesignateContradictionNeedsANullaryFunctionOnceAndBeforeAnyChild()
        {
            var g = new ContextGraph();
            Assert.Null(g.EGraph.Contradiction);
            Assert.Null(g.EGraph.StopOnContradiction);
            Assert.Throws<ArgumentException>(() => g.EGraph.DesignateContradiction(g.Mark));
            Assert.Throws<ArgumentException>(() => g.EGraph.DesignateContradiction(new FunctionId(99)));
            g.EGraph.DesignateContradiction(g.Bottom);
            Assert.Equal(g.Bottom, g.EGraph.Contradiction);
            Assert.Equal(g.EGraph.GetTable(g.Bottom), g.EGraph.Store.Schema.Contradiction);
            Assert.IsType<StopOnContradictionFunction>(g.EGraph.Database.GetExternalFunction(g.EGraph.StopOnContradiction!.Value));
            Assert.Throws<InvalidOperationException>(() => g.EGraph.DesignateContradiction(g.Bottom));

            var late = new ContextGraph();
            late.Root.CreateChild().Dispose();
            Assert.Throws<InvalidOperationException>(() => late.EGraph.DesignateContradiction(late.Bottom));
            Assert.Null(late.EGraph.Contradiction);
        }

        /// <summary>
        /// In a case, a rule that derives the contradiction stops the context: the run reports it, the context answers
        /// only whether it is contradictory, and later runs and flushes do nothing (L4, X11, rule-layer D45).
        /// </summary>
        [Fact]
        public void ACaseStopsWhenARuleDerivesTheContradiction()
        {
            var g = new ContextGraph();
            g.EGraph.DesignateContradiction(g.Bottom);
            var rule = g.MarkIsBottom();
            g.Root.Freeze();

            using var @case = g.Root.CreateChild(ContextOptions.Case);
            g.Assert(@case, g.Mark, @case.FreshId());
            @case.FlushUpdates();
            Assert.False(@case.IsStopped);
            var report = @case.RunRules(rule);
            Assert.True(report.Stopped);
            Assert.True(report.Changed);
            Assert.True(@case.IsStopped);
            Assert.True(@case.IsContradictory);
            Assert.Same(IterationReport.WhenStopped, @case.RunRules(rule));
            Assert.False(@case.FlushUpdates());
            Assert.Equal(new SaturateReport(1, true, false), @case.Saturate([rule], 10));
            @case.CheckInvariants();
        }

        /// <summary>
        /// The early stop (§10.6): the insert into the contradiction function stops a case's search, so the rules after it
        /// match nothing, while in an ordinary child the contradiction is a fact like any other and every rule runs.
        /// </summary>
        [Fact]
        public void TheEarlyStopCutsACasesSearchShort()
        {
            var g = new ContextGraph();
            g.EGraph.DesignateContradiction(g.Bottom);
            var bottom = g.MarkIsBottom();
            var copy = g.EdgeToPath();
            var ids = Enumerable.Range(0, 10).Select(_ => g.Root.FreshId()).ToArray();
            for (var i = 0; i + 1 < ids.Length; i++)
                g.Assert(g.Root, g.Edge, ids[i], ids[i + 1]);

            g.Root.FlushUpdates();
            g.Root.Freeze();

            using var @case = g.Root.CreateChild(ContextOptions.Case);
            using var plain = g.Root.CreateChild();

            // More marks than one action batch (Bindings.DefaultBatchSize): the first full batch runs the insert and its
            // early stop while the search is still going.
            const int Marks = 3 * Bindings.DefaultBatchSize;
            for (var i = 0; i < Marks; i++)
            {
                g.Assert(@case, g.Mark, @case.FreshId());
                g.Assert(plain, g.Mark, plain.FreshId());
            }

            @case.FlushUpdates();
            plain.FlushUpdates();

            var stopped = @case.RunRules(bottom, copy);
            Assert.True(stopped.Stopped);
            Assert.True(stopped.RuleSetReport.NumMatches("mark-bottom") < Marks);
            Assert.Equal(0, stopped.RuleSetReport.NumMatches("edge-path"));

            var ordinary = plain.RunRules(bottom, copy);
            Assert.False(ordinary.Stopped);
            Assert.False(plain.IsStopped);
            Assert.True(plain.IsContradictory);
            Assert.Equal(Marks, ordinary.RuleSetReport.NumMatches("mark-bottom"));
            Assert.Equal(9, ordinary.RuleSetReport.NumMatches("edge-path"));
            Assert.Equal(9, plain.TableSize(g.Path));
            plain.CheckInvariants();
        }

        /// <summary>
        /// At the root a contradiction is an ordinary fact (D11): the early-stop function does nothing there, and runs
        /// continue.
        /// </summary>
        [Fact]
        public void TheRootNeverStops()
        {
            var g = new ContextGraph();
            g.EGraph.DesignateContradiction(g.Bottom);
            var bottom = g.MarkIsBottom();
            var copy = g.EdgeToPath();
            g.Assert(g.Root, g.Edge, g.Root.FreshId(), g.Root.FreshId());
            g.Assert(g.Root, g.Mark, g.Root.FreshId());
            g.Root.FlushUpdates();

            var report = g.EGraph.RunRules(bottom, copy);
            Assert.False(report.Stopped);
            Assert.Equal(1, report.RuleSetReport.NumMatches("edge-path"));
            Assert.True(g.Root.IsContradictory);
            Assert.False(g.Root.IsStopped);
            Assert.False(g.Root.RunRules(bottom, copy).Stopped);
        }

        /// <summary>
        /// A case created where the contradiction is already visible starts stopped (§6.9 item 3), and a case under a
        /// stopped writable case stops with it, while an ordinary child of a stopped case is refused, unfaulted.
        /// </summary>
        [Fact]
        public void CasesUnderAContradictionStop()
        {
            var g = new ContextGraph();
            g.EGraph.DesignateContradiction(g.Bottom);
            var rule = g.MarkIsBottom();
            g.Root.Freeze();

            using var parent = g.Root.CreateChild(ContextOptions.Case);
            var inner = parent.CreateChild(ContextOptions.Case);
            var ordinary = parent.CreateChild();
            g.Assert(parent, g.Mark, parent.FreshId());
            parent.FlushUpdates();
            Assert.True(parent.RunRules(rule).Stopped);

            Assert.True(inner.RunRules(rule).Stopped || inner.IsStopped);
            Assert.True(inner.IsStopped);
            Assert.Throws<InvalidOperationException>(() => ordinary.RunRules(rule));
            Assert.False(ordinary.IsFaulted);
            inner.Dispose();
            ordinary.Dispose();

            var g2 = new ContextGraph();
            g2.EGraph.DesignateContradiction(g2.Bottom);
            g2.Root.StageRow(g2.Bottom, [g2.Unit]);
            g2.Root.FlushUpdates();
            g2.Root.Freeze();
            using var born = g2.Root.CreateChild(ContextOptions.Case);
            Assert.True(born.IsStopped);
            Assert.True(born.IsContradictory);
            Assert.True(born.RunRules().Stopped);
        }

        /// <summary>
        /// A rule planned before the designation keeps its plan without the early stop (§10.6): the case still stops when
        /// the contradiction row merges, but the rest of the search runs.
        /// </summary>
        [Fact]
        public void ARulePlannedBeforeTheDesignationStopsOnlyAtTheMerge()
        {
            var g = new ContextGraph();
            var bottom = g.MarkIsBottom();
            var copy = g.EdgeToPath();
            g.Root.RunRules(bottom, copy);
            g.EGraph.DesignateContradiction(g.Bottom);
            g.Assert(g.Root, g.Edge, g.Root.FreshId(), g.Root.FreshId());
            g.Root.FlushUpdates();
            g.Root.Freeze();

            using var @case = g.Root.CreateChild(new ContextOptions { IsCase = true, StopOnContradiction = true, InheritWatermarks = false });
            const int Marks = 3 * Bindings.DefaultBatchSize;
            for (var i = 0; i < Marks; i++)
                g.Assert(@case, g.Mark, @case.FreshId());

            @case.FlushUpdates();
            var report = @case.RunRules(bottom, copy);
            Assert.True(report.Stopped);
            Assert.Equal(Marks, report.RuleSetReport.NumMatches("mark-bottom"));
            Assert.Equal(1, report.RuleSetReport.NumMatches("edge-path"));
        }

    }

}

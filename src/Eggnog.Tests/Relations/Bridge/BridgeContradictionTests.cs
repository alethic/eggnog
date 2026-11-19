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
    /// Contradiction in the bridge: designating the contradiction function (atomically with child creation), a child
    /// created to stop on contradictions (<see cref="ContextOptions.StopOnContradiction"/>), which stops at the merge
    /// after its whole search, or at the catch-up a flush makes before it merges, the stop flag a run reports, and
    /// contradiction as an ordinary fact elsewhere. Named apart from the storage <c>ContradictionTests</c>.
    /// </summary>
    public class BridgeContradictionTests
    {

        [Fact]
        public void DesignateContradictionNeedsANullaryFunctionOnceAndBeforeAnyChild()
        {
            var g = new ContextGraph();
            Assert.Null(g.EGraph.Contradiction);
            Assert.Throws<ArgumentException>(() => g.EGraph.DesignateContradiction(g.Mark));
            Assert.Throws<ArgumentException>(() => g.EGraph.DesignateContradiction(new FunctionId(99)));
            g.EGraph.DesignateContradiction(g.Bottom);
            Assert.Equal(g.Bottom, g.EGraph.Contradiction);
            Assert.Equal(g.EGraph.GetTable(g.Bottom), g.EGraph.Store.Schema.Contradiction);
            Assert.Throws<InvalidOperationException>(() => g.EGraph.DesignateContradiction(g.Bottom));

            var late = new ContextGraph();
            late.Root.CreateChild().Dispose();
            Assert.Throws<InvalidOperationException>(() => late.EGraph.DesignateContradiction(late.Bottom));
            Assert.Null(late.EGraph.Contradiction);
        }

        /// <summary>
        /// In a child that stops on contradictions, a rule that derives the contradiction stops the context: the run
        /// reports it, the context answers only whether it is contradictory, and later runs and flushes do nothing.
        /// </summary>
        [Fact]
        public void AStoppingChildStopsWhenARuleDerivesTheContradiction()
        {
            var g = new ContextGraph();
            g.EGraph.DesignateContradiction(g.Bottom);
            var rule = g.MarkIsBottom();
            g.Root.Freeze();

            using var stopping = g.Root.CreateChild(new ContextOptions { StopOnContradiction = true });
            g.Assert(stopping, g.Mark, stopping.FreshId());
            stopping.FlushUpdates();
            Assert.False(stopping.IsStopped);
            var report = stopping.RunRules(rule);
            Assert.True(report.Stopped);
            Assert.True(report.Changed);
            Assert.True(stopping.IsStopped);
            Assert.True(stopping.IsContradictory);
            Assert.Same(IterationReport.WhenStopped, stopping.RunRules(rule));
            Assert.False(stopping.FlushUpdates());
            Assert.Equal(new SaturateReport(1, true, false), stopping.Saturate([rule], 10));
            stopping.CheckInvariants();
        }

        /// <summary>
        /// A child that stops on contradictions stops at the merge, after its whole search: the insert into the
        /// contradiction function is an ordinary write, so every rule of the run matches in full before the merge that
        /// applies the row stops the child, while in an ordinary child the contradiction is a fact like any other and
        /// the child goes on.
        /// </summary>
        [Fact]
        public void AStoppingChildsSearchRunsInFullBeforeTheMergeStopsIt()
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

            using var stopping = g.Root.CreateChild(new ContextOptions { StopOnContradiction = true });
            using var plain = g.Root.CreateChild();

            // More marks than one action batch (Bindings.DefaultBatchSize): the batches after the one that first writes
            // the contradiction still run.
            const int Marks = 3 * Bindings.DefaultBatchSize;
            for (var i = 0; i < Marks; i++)
            {
                g.Assert(stopping, g.Mark, stopping.FreshId());
                g.Assert(plain, g.Mark, plain.FreshId());
            }

            stopping.FlushUpdates();
            plain.FlushUpdates();

            var stopped = stopping.RunRules(bottom, copy);
            Assert.True(stopped.Stopped);
            Assert.True(stopping.IsStopped);
            Assert.Equal(Marks, stopped.RuleSetReport.NumMatches("mark-bottom"));
            Assert.Equal(9, stopped.RuleSetReport.NumMatches("edge-path"));

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
        /// A child of the writable root, created to stop on contradictions, stages a row, and the root merges the
        /// contradiction row: the child's flush, which catches up with the root before it stages anything, stops the
        /// child there and returns without merging, the child stopped and unfaulted.
        /// </summary>
        [Fact]
        public void AStoppingChildStopsWhenItsFlushCatchesUpAContradiction()
        {
            var g = new ContextGraph();
            g.EGraph.DesignateContradiction(g.Bottom);
            using var stopping = g.Root.CreateChild(new ContextOptions { StopOnContradiction = true });
            g.Assert(stopping, g.Mark, stopping.FreshId());
            g.Root.StageRow(g.Bottom, [g.Unit]);
            g.Root.FlushUpdates();

            Assert.False(stopping.FlushUpdates());
            Assert.True(stopping.IsStopped);
            Assert.True(stopping.IsContradictory);
            Assert.False(stopping.IsFaulted);
            stopping.CheckInvariants();
        }

        /// <summary>
        /// At the root a contradiction is an ordinary fact: runs continue.
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
        /// A child that stops on contradictions, created where the contradiction is already visible, starts stopped,
        /// and one under a stopped writable child stops with it, while an ordinary child of a stopped context is
        /// refused, unfaulted.
        /// </summary>
        [Fact]
        public void StoppingChildrenUnderAContradictionStop()
        {
            var g = new ContextGraph();
            g.EGraph.DesignateContradiction(g.Bottom);
            var rule = g.MarkIsBottom();
            g.Root.Freeze();

            using var parent = g.Root.CreateChild(new ContextOptions { StopOnContradiction = true });
            var inner = parent.CreateChild(new ContextOptions { StopOnContradiction = true });
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
            using var born = g2.Root.CreateChild(new ContextOptions { StopOnContradiction = true });
            Assert.True(born.IsStopped);
            Assert.True(born.IsContradictory);
            Assert.True(born.RunRules().Stopped);
        }

        /// <summary>
        /// A context stopped second from the top of a 40-deep chain of children that stop on contradictions stops
        /// every child below it when the deepest is brought up to date, level by level from the top, while the child
        /// above it stays as it was; an ordinary child at the bottom is refused, unfaulted.
        /// </summary>
        [Fact]
        public void AChildStoppedHalfwayUpADeepChainStopsTheStoppingChildrenBelowIt()
        {
            const int Depth = 40;
            var g = new ContextGraph();
            g.EGraph.DesignateContradiction(g.Bottom);
            var rule = g.MarkIsBottom();
            g.Root.Freeze();

            var stopping = new ContextOptions { StopOnContradiction = true };
            var chain = new List<EGraphContext>();
            var parent = g.Root;
            for (var d = 1; d <= Depth; d++)
            {
                parent = parent.CreateChild(stopping);
                chain.Add(parent);
            }

            var ordinary = chain[^1].CreateChild();
            chain[^1].EnsureClean();
            Assert.True(chain[^1].IsClean);
            g.Assert(chain[1], g.Mark, chain[1].FreshId());
            chain[1].FlushUpdates();
            Assert.True(chain[1].RunRules(rule).Stopped);

            chain[^1].EnsureClean();
            Assert.False(chain[0].IsStopped);
            Assert.All(chain.Skip(1), child => Assert.True(child.IsStopped));
            Assert.All(chain, child => Assert.False(child.IsFaulted));
            Assert.Throws<InvalidOperationException>(ordinary.EnsureClean);
            Assert.False(ordinary.IsFaulted);

            ordinary.Dispose();
            for (var d = Depth - 1; d >= 0; d--)
                chain[d].Dispose();
        }

        /// <summary>
        /// A plan does not depend on the designation: a rule planned before it stops a child that stops on
        /// contradictions as one planned after does, when the contradiction row merges, after the whole search.
        /// </summary>
        [Fact]
        public void ARulePlannedBeforeTheDesignationStopsAChildAlike()
        {
            var g = new ContextGraph();
            var bottom = g.MarkIsBottom();
            var copy = g.EdgeToPath();
            g.Root.RunRules(bottom, copy);
            g.EGraph.DesignateContradiction(g.Bottom);
            g.Assert(g.Root, g.Edge, g.Root.FreshId(), g.Root.FreshId());
            g.Root.FlushUpdates();
            g.Root.Freeze();

            using var stopping = g.Root.CreateChild(new ContextOptions { StopOnContradiction = true, InheritWatermarks = false });
            const int Marks = 3 * Bindings.DefaultBatchSize;
            for (var i = 0; i < Marks; i++)
                g.Assert(stopping, g.Mark, stopping.FreshId());

            stopping.FlushUpdates();
            var report = stopping.RunRules(bottom, copy);
            Assert.True(report.Stopped);
            Assert.Equal(Marks, report.RuleSetReport.NumMatches("mark-bottom"));
            Assert.Equal(1, report.RuleSetReport.NumMatches("edge-path"));
        }

        /// <summary>
        /// Designating the contradiction is atomic with child creation: a designation racing the first child of a
        /// frozen root either fails, or lands before the child's creation check, which then sees the contradiction row
        /// the root holds and stops the child, created to stop on contradictions. A designation never succeeds beside
        /// such a child that missed it.
        /// </summary>
        [Fact]
        public void ADesignationRacingTheFirstChildIsSeenByItOrRefused()
        {
            var designated = 0;
            var refused = 0;
            for (var i = 0; i < 200; i++)
            {
                var g = new ContextGraph();
                g.Assert(g.Root, g.Bottom);
                g.Root.FlushUpdates();
                g.Root.Freeze();

                using var barrier = new Barrier(2);
                EGraphContext? child = null;
                var create = Task.Run(() =>
                {
                    barrier.SignalAndWait();
                    child = g.Root.CreateChild(new ContextOptions { StopOnContradiction = true });
                });
                var designate = Task.Run(() =>
                {
                    barrier.SignalAndWait();
                    try
                    {
                        g.EGraph.DesignateContradiction(g.Bottom);
                        return true;
                    }
                    catch (InvalidOperationException)
                    {
                        return false;
                    }
                });

                create.Wait();
                var won = designate.Result;
                using (child)
                {
                    if (won)
                    {
                        designated++;
                        Assert.True(child!.IsStopped, $"iteration {i}: the designation succeeded, yet the child did not stop");
                    }
                    else
                    {
                        refused++;
                        Assert.False(child!.IsStopped);
                    }
                }
            }

            Assert.Equal(200, designated + refused);
        }

    }

}

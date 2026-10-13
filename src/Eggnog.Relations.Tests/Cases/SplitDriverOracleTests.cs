using System;
using System.Linq;

using Xunit;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// Split-driver behaviours (colored-contexts spec §11.3, rule-layer E8) that <see cref="SplitDriverTests"/> leaves
    /// unchecked: the phase B lift, the end of a step whose lift contradicts the scope, the disposal of cases a lift finds
    /// vacuous, and the record a failed <c>Assume</c> leaves for <see cref="SplitDriver.DisposeRecords"/>.
    /// </summary>
    public class SplitDriverOracleTests
    {

        /// <summary>
        /// Phase B (§11.3 step 7): every top case gains <c>mark(d)</c> only from its own nested split of d (both nested
        /// cases assert it, and phase A of the nested step lifts it into the top case). The top lift of phase A runs
        /// before that, so only the lift after deepening brings <c>mark(d)</c> into the root.
        /// </summary>
        [Fact]
        public void PhaseBLiftsWhatNestedSplitsBroughtUp()
        {
            var w = new SplitWorld();
            var g = w.G;
            var nested = new TestSplitSource(1, scope => scope.IsRoot ? [] : [([w.D.Rep], 2)], (@case, _, _) => g.Assert(@case, g.Mark, w.D));
            SplitDriver? driver = null;
            var report = new SplitReport();
            var schedule = new RecordingSchedule((@case, depth, allowSplits) => allowSplits && driver!.Step(@case, depth, report));
            driver = new SplitDriver([w.BooleanOfB(0), nested], schedule, null, w.Spec, new SplitOptions { DepthLimit = 3 });

            Assert.True(driver.Step(g.Root, 0, report));
            Assert.Equal(6, report.CasesOpened);
            Assert.True(g.Root.ContainsKey(g.Mark, [w.D]));
            Assert.True(g.Root.ContainsKey(g.Edge, [w.B, w.C]));
            SplitDriver.DisposeRecords(g.Root);
            Assert.Empty(g.Root.Context.SnapshotChildren());
        }

        /// <summary>
        /// A lift that contradicts the scope ends the step: the scope (a case) stops, and the step's later record is
        /// neither run nor lifted into the stopped scope; its cases are left for <see cref="SplitDriver.DisposeRecords"/>.
        /// </summary>
        [Fact]
        public void AContradictingLiftEndsTheStep()
        {
            var w = new SplitWorld();
            var g = w.G;
            g.Root.Freeze();
            using var scope = g.Root.CreateChild(ContextOptions.Case);
            var dead = new TestSplitSource(0, _ => [([w.D.Rep], 2)], (@case, _, _) => @case.StageRow(g.Bottom, [g.Unit]));
            var schedule = new RecordingSchedule();
            var driver = new SplitDriver([dead, w.BooleanOfB(1)], schedule, null, w.Spec, SplitOptions.Default);
            var report = new SplitReport();

            Assert.True(driver.Step(scope, 0, report));
            Assert.True(scope.IsStopped);
            Assert.Equal(1, report.Lifts);
            Assert.Equal(4, report.CasesOpened);
            Assert.Empty(schedule.Calls);
            Assert.Equal(2, scope.SplitRecordCount);
            SplitDriver.DisposeRecords(scope);
            Assert.Empty(scope.Context.SnapshotChildren());
        }

        /// <summary>
        /// A case that becomes contradictory while its schedule runs in phase A is left out of the lift (L4) and disposed
        /// after it; the live case's facts are lifted.
        /// </summary>
        [Fact]
        public void ACaseThatTurnsVacuousInItsRunIsDisposedAfterTheLift()
        {
            var w = new SplitWorld();
            var g = w.G;
            EGraphContext? turned = null;
            var schedule = new RecordingSchedule((@case, _, _) =>
            {
                if (@case.Find(w.B) == @case.Find(w.F))
                {
                    turned = @case;
                    @case.StageRow(g.Bottom, [g.Unit]);
                    @case.FlushUpdates();
                }

                return false;
            });
            var driver = new SplitDriver([w.BooleanOfB(0)], schedule, null, w.Spec, new SplitOptions { DepthLimit = 1 });

            Assert.True(driver.Step(g.Root, 0, new SplitReport()));
            Assert.Equal(2, schedule.Calls.Count);
            Assert.NotNull(turned);
            Assert.True(turned!.IsDisposed);
            Assert.Single(g.Root.Context.SnapshotChildren());
            Assert.Equal(g.Root.Find(w.B), g.Root.Find(w.T));
            Assert.False(g.Root.IsContradictory);
            SplitDriver.DisposeRecords(g.Root);
            Assert.Empty(g.Root.Context.SnapshotChildren());
        }

        /// <summary>
        /// A source whose <c>Assume</c> throws: the exception propagates, and the split's record is already registered, so
        /// <see cref="SplitDriver.DisposeRecords"/> disposes the cases opened before the failure (E8: exceptions propagate
        /// and the records stay for <c>DisposeRecords</c>).
        /// </summary>
        [Fact]
        public void AnAssumeFailureLeavesTheRecordForDisposal()
        {
            var w = new SplitWorld();
            var g = w.G;
            var failing = new TestSplitSource(0, _ => [([w.B.Rep], 2)], (_, i, _) =>
            {
                if (i == 1)
                    throw new InvalidOperationException("assume failed");
            });
            var driver = new SplitDriver([failing], new RecordingSchedule(), null, w.Spec, SplitOptions.Default);

            Assert.Throws<InvalidOperationException>(() => driver.Step(g.Root, 0, new SplitReport()));
            Assert.Equal(1, g.Root.SplitRecordCount);
            Assert.Equal(2, g.Root.Context.SnapshotChildren().Length);
            SplitDriver.DisposeRecords(g.Root);
            Assert.Empty(g.Root.Context.SnapshotChildren());
            Assert.False(g.Root.IsFaulted);
        }

    }

}

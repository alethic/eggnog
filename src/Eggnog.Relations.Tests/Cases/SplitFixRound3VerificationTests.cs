using System.Collections.Generic;

using Xunit;

using static Eggnog.Relations.Tests.BridgeTestUtil;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// Third verification of the colors M10.4 fixes (colored-contexts spec §11.2, §11.3; rule-layer §6.9.3): split steps
    /// that revisit the scope's live records when more than one driver serves the scope, and the all-vacuous contradiction
    /// when a case set disposes only some of its vacuous cases before the lift.
    /// </summary>
    public class SplitFixRound3VerificationTests
    {

        /// <summary>
        /// The rule layer caches one <see cref="SplitDriver"/> per split schedule node (rule-layer §6.9.3,
        /// <c>executor.DriverFor(this)</c>), so a program such as <c>Saturate(Split(r1, inCaseA), Split(r2, inCaseB))</c>
        /// has two drivers over one scope. Since the revisit fix, a step runs "every live record of the scope", and the
        /// records on a scope do not say which driver opened them. Driver B's step must not run driver B's in-case schedule
        /// in the cases of driver A's split: those cases were opened for split A, whose in-case schedule is A's.
        /// </summary>
        [Fact]
        public void AStepRunsItsScheduleOnlyInTheCasesOfItsOwnSplits()
        {
            var w = new SplitWorld();
            var root = w.G.Root;
            var sourceA = w.BooleanOfB(0);
            var sourceB = new TestSplitSource(0, _ => [([w.D.Rep], 2)], (@case, i, _) => @case.StageUnion(w.D, i == 0 ? w.T : w.F));
            var scheduleA = new RecordingSchedule();
            var scheduleB = new RecordingSchedule();
            var driverA = new SplitDriver([sourceA], scheduleA, null, w.Spec, SplitOptions.Default);
            var driverB = new SplitDriver([sourceB], scheduleB, null, w.Spec, SplitOptions.Default);

            try
            {
                Assert.True(driverA.Step(root, 0, new SplitReport()));
                var records = new List<SplitRecord>();
                root.CopyLiveSplitRecords(records);
                Assert.Single(records);
                var casesOfA = new HashSet<EGraphContext>(records[0].Cases.Cases);

                Assert.True(driverB.Step(root, 0, new SplitReport()));
                foreach (var (@case, _, _) in scheduleB.Calls)
                    Assert.False(casesOfA.Contains(@case), "driver B ran its in-case schedule in a case of driver A's split");
            }
            finally
            {
                SplitDriver.DisposeRecords(root);
            }
        }

        /// <summary>
        /// B6 skips a request whose key a live record covers. A <see cref="SplitKey"/> names its source by index into its
        /// own driver's list, so the first source of one driver and the first source of another give equal keys for the same
        /// values. Driver B's split of <c>b</c> over <c>{c, d}</c> is a different split from driver A's split of <c>b</c>
        /// over <c>{t, f}</c>; a live record of A must not make B skip it.
        /// </summary>
        [Fact]
        public void AnotherDriversRecordDoesNotCoverASplitOfADifferentSource()
        {
            var w = new SplitWorld();
            var root = w.G.Root;
            var sourceA = w.BooleanOfB(0);
            var sourceB = new TestSplitSource(0, _ => [([w.B.Rep], 2)], (@case, i, _) => @case.StageUnion(w.B, i == 0 ? w.C : w.D));
            var driverA = new SplitDriver([sourceA], new RecordingSchedule(), null, w.Spec, SplitOptions.Default);
            var driverB = new SplitDriver([sourceB], new RecordingSchedule(), null, w.Spec, SplitOptions.Default);

            try
            {
                Assert.True(driverA.Step(root, 0, new SplitReport()));
                var report = new SplitReport();
                driverB.Step(root, 0, report);
                Assert.True(report.CasesOpened == 2, $"driver B's split of b over {{c, d}} was skipped as covered by driver A's split of b over {{t, f}} (opened {report.CasesOpened}, skipped {report.SplitsSkipped})");
            }
            finally
            {
                SplitDriver.DisposeRecords(root);
            }
        }

        /// <summary>
        /// L4 with a base-output contradiction function and no constant default, mixing the two paths: the set disposes the
        /// first vacuous case (<see cref="CaseSet.DisposeVacuous"/>), keeping its output, while the second case is still
        /// live; the second case then goes vacuous and is either lifted undisposed (<c>disposeSecond</c> false) or disposed
        /// by the set first. Either way the lift contradicts the parent.
        /// </summary>
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void AnAllVacuousSetDisposedInTwoStepsContradictsTheParent(bool disposeSecond)
        {
            var g = new ContextGraph();
            var bad = g.EGraph.AddTable(new FunctionConfig
            {
                Schema = [g.I64],
                Default = DefaultVal.Fail,
                Merge = MergeFn.Primitive(g.Min, MergeFn.Old, MergeFn.New).AsLattice(),
                Name = "bad",
            });
            g.EGraph.DesignateContradiction(bad);
            _ = g.Root.FreshId();

            using var set = new CaseSet(g.Root);
            var first = set.AddCase();
            var second = set.AddCase();
            first.StageRow(bad, [Int(g.EGraph, 3)]);
            first.EnsureClean();
            Assert.Equal(1, set.DisposeVacuous());
            Assert.Single(set.Cases);

            second.StageRow(bad, [Int(g.EGraph, 5)]);
            second.EnsureClean();
            if (disposeSecond)
                Assert.Equal(1, set.DisposeVacuous());

            var report = set.Lift(LiftSpec.Empty, LiftOptions.Default);
            Assert.True(report.ParentContradicted);
            Assert.True(g.Root.IsContradictory);
            g.Root.CheckInvariants();
        }

    }

}

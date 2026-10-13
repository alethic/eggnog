using System.Collections.Generic;

using Xunit;

using static Eggnog.Relations.Tests.BridgeTestUtil;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// Several <see cref="SplitDriver"/>s over one scope: each driver runs and lifts only the records it opened, and a
    /// record covers a request of another driver only when both come from the same source object.
    /// </summary>
    public class SplitDriverSharingTests
    {

        /// <summary>
        /// Two drivers sharing one source object: the second driver's request is covered by the first driver's live
        /// record (the coverage check compares the source by reference), so it opens nothing and never runs its
        /// schedule in the first driver's cases; the first driver's next step still revisits its own record.
        /// </summary>
        [Fact]
        public void ASharedSourceIsCoveredAcrossDriversAndEachDriverRevisitsOnlyItsOwnRecords()
        {
            var w = new SplitWorld();
            var root = w.G.Root;
            var shared = w.BooleanOfB(0);
            var scheduleA = new RecordingSchedule();
            var scheduleB = new RecordingSchedule();
            var driverA = new SplitDriver([shared], scheduleA, null, w.Spec, SplitOptions.Default);
            var driverB = new SplitDriver([shared], scheduleB, null, w.Spec, SplitOptions.Default);

            try
            {
                Assert.True(driverA.Step(root, 0, new SplitReport()));
                var records = new List<SplitRecord>();
                root.CopyLiveSplitRecords(records);
                var record = Assert.Single(records);
                Assert.Same(driverA, record.Driver);

                var reportB = new SplitReport();
                Assert.False(driverB.Step(root, 0, reportB));
                Assert.Equal(0, reportB.CasesOpened);
                Assert.Equal(1, reportB.SplitsSkipped);
                Assert.Equal(0, reportB.Lifts);
                Assert.Empty(scheduleB.Calls);

                var ownOfB = new List<SplitRecord>();
                root.CopyLiveSplitRecords(ownOfB, driverB);
                Assert.Empty(ownOfB);

                var before = scheduleA.Calls.Count;
                var reportA = new SplitReport();
                driverA.Step(root, 0, reportA);
                Assert.Equal(0, reportA.CasesOpened);
                Assert.True(reportA.Lifts >= 1);
                var cases = new HashSet<EGraphContext>(record.Cases.Cases);
                Assert.True(scheduleA.Calls.Count > before);
                for (var i = before; i < scheduleA.Calls.Count; i++)
                    Assert.Contains(scheduleA.Calls[i].Case, cases);
            }
            finally
            {
                SplitDriver.DisposeRecords(root);
            }
        }

        /// <summary>
        /// The rule layer caches one <see cref="SplitDriver"/> per split schedule node
        /// (<c>executor.DriverFor(this)</c>), so a program such as <c>Saturate(Split(r1, inCaseA), Split(r2,
        /// inCaseB))</c> has two drivers over one scope. A step revisits the live records of the scope that its own
        /// driver opened: driver B's step does not run driver B's in-case schedule in the cases of driver A's split,
        /// which were opened for split A, whose in-case schedule is A's.
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
        /// A step skips a request whose key a live record covers. A <see cref="SplitKey"/> names its source by index
        /// into its own driver's list, so the first source of one driver and the first source of another give equal
        /// keys for the same values; coverage also requires the same source object. Driver B's split of <c>b</c> over
        /// <c>{c, d}</c> is a different split from driver A's split of <c>b</c> over <c>{t, f}</c>, and a live record
        /// of A does not make B skip it.
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

    }

}

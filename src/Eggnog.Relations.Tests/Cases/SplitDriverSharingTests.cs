using System.Collections.Generic;

using Xunit;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// Several <see cref="SplitDriver"/>s over one scope (colored-contexts spec §11.3 step 6, B6; rule-layer §6.9.3): each
    /// driver runs and lifts only the records it opened, and a record covers a request of another driver only when both
    /// come from the same source object.
    /// </summary>
    public class SplitDriverSharingTests
    {

        /// <summary>
        /// Two drivers sharing one source object: the second driver's request is covered by the first driver's live record
        /// (B6 compares the source by reference), so it opens nothing and never runs its schedule in the first driver's
        /// cases; the first driver's next step still revisits its own record.
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

    }

}

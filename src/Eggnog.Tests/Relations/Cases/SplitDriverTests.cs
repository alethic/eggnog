using System;
using System.Collections.Generic;
using System.Linq;

using Xunit;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// The split driver: lift before deepening, demand before any case exists, keys covered by live records in the
    /// scope or an ancestor, limits, depth-first disposal, vacuous cases dropped, exceptions that leave the records for
    /// disposal, and the domain never held while sources and schedules run.
    /// </summary>
    public class SplitDriverTests
    {

        /// <summary>
        /// Lift before deepening, as in TheSy: phase A runs every case with nested splits off and lifts before phase B
        /// runs any case with them on, so phase B (and the nested split it opens) already sees what phase A lifted;
        /// nested steps skip the key of the split they are a case of, which the ancestor's live record covers.
        /// </summary>
        [Fact]
        public void PhaseALiftsBeforePhaseBDeepens()
        {
            var w = new SplitWorld();
            var g = w.G;
            var log = new List<string>();
            var sources = new List<ISplitSource> { w.BooleanOfB(0), w.SplitOfD(1) };
            foreach (var s in sources.Cast<TestSplitSource>())
                s.Log = log;

            SplitDriver? driver = null;
            var report = new SplitReport();
            var seenLiftedInPhaseB = new List<bool>();
            var schedule = new RecordingSchedule((@case, depth, allowSplits) =>
            {
                Assert.Equal(0, @case.Context.Domain.Depth);
                var changed = @case.Saturate([w.Copy], 4).Iterations > 1;
                if (allowSplits)
                {
                    seenLiftedInPhaseB.Add(g.Root.ContainsKey(g.Path, [w.B, w.C]));
                    changed |= driver!.Step(@case, depth, report);
                }

                return changed;
            }) { Log = log };
            driver = new SplitDriver(sources, schedule, null, w.Spec, new SplitOptions { DepthLimit = 3 });

            Assert.True(driver.Step(g.Root, 0, report));

            // Phase A's two runs come before any phase B run, and the top lift happened in between.
            var firstB = log.IndexOf("run d1 B");
            Assert.True(firstB > 0);
            Assert.Equal(2, log.Take(firstB).Count(l => l == "run d0 A"));
            Assert.NotEmpty(seenLiftedInPhaseB);
            Assert.All(seenLiftedInPhaseB, Assert.True);
            Assert.True(g.Root.ContainsKey(g.Path, [w.B, w.C]));
            Assert.True(g.Root.ContainsKey(g.Edge, [w.B, w.C]));

            // The top split opened 2 cases, each case's nested split of d 2 more; nested steps skipped b (covered by
            // the root's record) and, at depth 2, both keys.
            Assert.Equal(6, report.CasesOpened);
            Assert.True(report.SplitsSkipped >= 2);
            Assert.True(report.Lifts >= 3);
            Assert.Equal(0, sources.Cast<TestSplitSource>().Sum(s => s.CallsWithDomainHeld));

            var top = g.Root.Context.SnapshotChildren();
            Assert.Equal(2, top.Length);
            Assert.All(top, c => Assert.Equal(1, ((EGraphContext)c.Host!).SplitRecordCount));
            SplitDriver.DisposeRecords(g.Root);
            Assert.Empty(g.Root.Context.SnapshotChildren());
            Assert.Equal(0, g.Root.SplitRecordCount);
        }

        /// <summary>
        /// Demand-driven splitting: a request the demand rejects opens no case; the demand is asked before any case
        /// exists.
        /// </summary>
        [Fact]
        public void TheDemandFiltersBeforeAnyCaseExists()
        {
            var w = new SplitWorld();
            var g = w.G;
            var demand = new TestSplitDemand(r =>
            {
                Assert.Empty(g.Root.Context.SnapshotChildren());
                return r.Key.Source == 1;
            });
            var ofD = new TestSplitSource(1, _ => [([w.D.Rep], 2)], (@case, _, _) => g.Assert(@case, g.Mark, w.D));
            var driver = new SplitDriver([w.BooleanOfB(0), ofD], new RecordingSchedule(), demand, w.Spec, SplitOptions.Default);
            var report = new SplitReport();
            Assert.True(driver.Step(g.Root, 0, report));
            Assert.Equal(2, demand.Calls);
            Assert.Equal(1, report.SplitsSkipped);
            Assert.Equal(2, report.CasesOpened);
            Assert.Equal(1, g.Root.SplitRecordCount);
            SplitDriver.DisposeRecords(g.Root);

            var none = new SplitDriver([w.BooleanOfB(0)], new RecordingSchedule(), new TestSplitDemand(_ => false), w.Spec, SplitOptions.Default);
            var skipped = new SplitReport();
            Assert.False(none.Step(g.Root, 0, skipped));
            Assert.Equal(1, skipped.SplitsSkipped);
            Assert.Empty(g.Root.Context.SnapshotChildren());
        }

        /// <summary>
        /// A key covered by a live record in the scope or any ancestor is skipped, so a split already open is not
        /// opened again; a second request for one key in a step is skipped too; once the records are disposed the key
        /// is split again.
        /// </summary>
        [Fact]
        public void KeysCoveredByALiveRecordAreSkipped()
        {
            var w = new SplitWorld();
            var g = w.G;
            var twice = new TestSplitSource(0, _ => [([w.B.Rep], 2), ([w.B.Rep], 2)], (@case, i, _) => @case.StageUnion(w.B, i == 0 ? w.T : w.F));
            var driver = new SplitDriver([twice], new RecordingSchedule(), null, w.Spec, SplitOptions.Default);
            var report = new SplitReport();
            Assert.True(driver.Step(g.Root, 0, report));
            Assert.Equal(1, report.SplitsSkipped);
            Assert.Equal(2, report.CasesOpened);

            // The scope again, and a case under it: both covered.
            Assert.False(driver.Step(g.Root, 0, report));
            var @case = (EGraphContext)g.Root.Context.SnapshotChildren()[0].Host!;
            Assert.False(driver.Step(@case, 1, report));
            Assert.Equal(5, report.SplitsSkipped);
            Assert.Equal(2, report.CasesOpened);

            // A source that declines a request counts it itself.
            var declining = new TestSplitSource(0, _ => [([w.D.Rep], 0)], (_, _, _) => { });
            var other = new SplitDriver([declining], new RecordingSchedule(), null, w.Spec, SplitOptions.Default);
            Assert.False(other.Step(g.Root, 0, report));
            Assert.Equal(6, report.SplitsSkipped);

            SplitDriver.DisposeRecords(g.Root);
            Assert.True(driver.Step(g.Root, 0, report));
            Assert.Equal(4, report.CasesOpened);
            SplitDriver.DisposeRecords(g.Root);
        }

        /// <summary>
        /// Limits: a request with more cases than the case limit (or none) is skipped; a step at the depth limit does
        /// nothing; phase B is not entered when the next depth would reach the limit.
        /// </summary>
        [Fact]
        public void LimitsAreRespected()
        {
            var w = new SplitWorld();
            var g = w.G;
            var wide = new TestSplitSource(0, _ => [([w.B.Rep], 3), ([w.D.Rep], 2)], (@case, i, key) => g.Assert(@case, g.Mark, new Value(key[0])));
            var schedule = new RecordingSchedule();
            var driver = new SplitDriver([wide], schedule, null, w.Spec, new SplitOptions { CaseLimit = 2, DepthLimit = 1 });
            var report = new SplitReport();
            Assert.True(driver.Step(g.Root, 0, report));
            Assert.Equal(1, report.SplitsSkipped);
            Assert.Equal(2, report.CasesOpened);
            Assert.All(schedule.Calls, c => Assert.False(c.AllowSplits));
            Assert.Equal(2, schedule.Calls.Count);

            // Both cases mark d: the lift brings it up.
            Assert.True(g.Root.ContainsKey(g.Mark, [w.D]));

            Assert.False(driver.Step(g.Root, 1, report));
            Assert.Equal(2, report.CasesOpened);
            SplitDriver.DisposeRecords(g.Root);

            Assert.Throws<ArgumentOutOfRangeException>(() => new SplitOptions { DepthLimit = 0 });
            Assert.Throws<ArgumentOutOfRangeException>(() => new SplitOptions { CaseLimit = 0 });
            Assert.Throws<ArgumentException>(() => new SplitOptions { CaseOptions = ContextOptions.Default });
            Assert.Throws<ArgumentOutOfRangeException>(() => driver.Step(g.Root, -1, report));
        }

        /// <summary>
        /// Disposal is depth-first and in reverse creation order: <see cref="SplitDriver.DisposeRecords"/> succeeds
        /// over nested records (a case with live children could not be disposed first), leaves no record, and disposes
        /// every case at every depth.
        /// </summary>
        [Fact]
        public void RecordsAreDisposedDepthFirst()
        {
            var w = new SplitWorld();
            var g = w.G;
            SplitDriver? driver = null;
            var report = new SplitReport();
            var all = new List<EGraphContext>();
            var schedule = new RecordingSchedule((@case, depth, allowSplits) =>
            {
                all.Add(@case);
                return allowSplits && driver!.Step(@case, depth, report);
            });
            driver = new SplitDriver([w.BooleanOfB(0), w.SplitOfD(1)], schedule, null, w.Spec, new SplitOptions { DepthLimit = 2 });
            Assert.True(driver.Step(g.Root, 0, report));
            Assert.Equal(6, report.CasesOpened);
            Assert.Contains(all, c => c.Depth == 2);

            SplitDriver.DisposeRecords(g.Root);
            Assert.All(all, c => Assert.True(c.IsDisposed));
            Assert.Equal(0, g.Root.SplitRecordCount);
            Assert.Empty(g.Root.Context.SnapshotChildren());

            // Nothing to do the second time.
            SplitDriver.DisposeRecords(g.Root);
        }

        /// <summary>
        /// A case whose assumption contradicts it stops and is disposed before the schedule runs, the lift goes on over
        /// the live cases, and a split whose cases are all vacuous contradicts the scope and ends the step.
        /// </summary>
        [Fact]
        public void StoppedCasesAreDisposed()
        {
            var w = new SplitWorld();
            var g = w.G;
            var source = new TestSplitSource(0, _ => [([w.B.Rep], 3)], (@case, i, _) =>
            {
                if (i == 1)
                    @case.StageRow(g.Bottom, [g.Unit]);
                else
                    g.Assert(@case, g.Mark, w.B);
            });
            var schedule = new RecordingSchedule();
            var driver = new SplitDriver([source], schedule, null, w.Spec, SplitOptions.Default);
            Assert.True(driver.Step(g.Root, 0, new SplitReport()));
            Assert.Equal(2, schedule.Calls.Count(c => c.AllowSplits == false));
            Assert.True(g.Root.ContainsKey(g.Mark, [w.B]));
            var cases = g.Root.Context.SnapshotChildren();
            Assert.Equal(2, cases.Length);
            SplitDriver.DisposeRecords(g.Root);

            // Every case vacuous: the scope (a case itself) is contradicted, and the step reports it.
            w.G.Root.Freeze();
            using var scope = g.Root.CreateChild(ContextOptions.Case);
            var dead = new TestSplitSource(0, _ => [([w.D.Rep], 2)], (@case, _, _) => @case.StageRow(g.Bottom, [g.Unit]));
            var deadDriver = new SplitDriver([dead], new RecordingSchedule(), null, w.Spec, SplitOptions.Default);
            Assert.True(deadDriver.Step(scope, 0, new SplitReport()));
            Assert.True(scope.IsStopped);
            Assert.False(deadDriver.Step(scope, 0, new SplitReport()));
            SplitDriver.DisposeRecords(scope);
        }

        /// <summary>
        /// An exception from the schedule propagates, the records opened stay, and
        /// <see cref="SplitDriver.DisposeRecords"/> cleans them up; the scope is not faulted.
        /// </summary>
        [Fact]
        public void AScheduleExceptionPropagatesAndLeavesTheRecords()
        {
            var w = new SplitWorld();
            var g = w.G;
            var schedule = new RecordingSchedule((_, _, _) => throw new OperationCanceledException());
            var driver = new SplitDriver([w.BooleanOfB(0)], schedule, null, w.Spec, SplitOptions.Default);
            Assert.Throws<OperationCanceledException>(() => driver.Step(g.Root, 0, new SplitReport()));
            Assert.Equal(1, g.Root.SplitRecordCount);
            Assert.Equal(2, g.Root.Context.SnapshotChildren().Length);
            SplitDriver.DisposeRecords(g.Root);
            Assert.Empty(g.Root.Context.SnapshotChildren());
            Assert.False(g.Root.IsFaulted);
        }

        [Fact]
        public void BadArgumentsAreRefused()
        {
            var w = new SplitWorld();
            var g = w.G;
            var bad = new TestSplitSource(3, _ => [([w.B.Rep], 2)], (_, _, _) => { });
            var driver = new SplitDriver([bad], new RecordingSchedule(), null, w.Spec, SplitOptions.Default);
            Assert.Throws<InvalidOperationException>(() => driver.Step(g.Root, 0, new SplitReport()));
            Assert.Throws<ArgumentException>(() => new SplitDriver([null!], new RecordingSchedule(), null, w.Spec, SplitOptions.Default));

            g.Root.Freeze();
            var good = new SplitDriver([w.BooleanOfB(0)], new RecordingSchedule(), null, w.Spec, SplitOptions.Default);
            Assert.Throws<InvalidOperationException>(() => good.Step(g.Root, 0, new SplitReport()));
        }

        [Fact]
        public void SplitKeysCompareByContent()
        {
            var a = new SplitKey(1, [1, 2, 3]);
            var b = new SplitKey(1, [1, 2, 3]);
            var c = new SplitKey(2, [1, 2, 3]);
            var d = new SplitKey(1, [1, 2]);
            Assert.Equal(a, b);
            Assert.True(a == b);
            Assert.Equal(a.GetHashCode(), b.GetHashCode());
            Assert.NotEqual(a, c);
            Assert.True(a != d);
            Assert.Throws<ArgumentOutOfRangeException>(() => new SplitKey(-1, []));
        }

    }

}

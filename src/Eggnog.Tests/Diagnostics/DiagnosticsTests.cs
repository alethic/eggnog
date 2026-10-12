using System;

using Eggnog.Tests.Support;

using Xunit;

namespace Eggnog.Tests.Diagnostics
{

    /// <summary>
    /// What a run reports (D41): per-rule statistics, and the engine's search, merge and rebuild times.
    /// </summary>
    public sealed class DiagnosticsTests
    {

        [Fact]
        public void TheReportCountsEachRulesMatches()
        {
            var t = new NodeTheory();
            var copied = t.Graph.DefineRelation<long>("copied");
            var never = t.Graph.DefineRelation<long>("never");
            var copy = t.Rules.DefineRule("copy", r =>
            {
                var x = r.Var<long>("x");
                r.When(t.Num.Of(x));
                r.Then(copied.Of(x));
            });
            var idle = t.Rules.DefineRule("idle", r =>
            {
                var x = r.Var<long>("x");
                r.When(never.Of(x));
                r.Then(copied.Of(x));
            });
            t.Root.Assume(t.Num.Of(1L), t.Num.Of(2L), t.Num.Of(3L));

            var report = t.Root.Run(Schedule.Saturate(t.Rules));

            Assert.Equal(2, report.Iterations);
            Assert.Equal(2, report.Rules.Count);
            var copyStatistics = report.Rules[copy];
            Assert.Same(copy, copyStatistics.Rule);
            Assert.Equal(3, copyStatistics.Matches);
            Assert.Equal(1, copyStatistics.IterationsWithMatches);
            Assert.True(copyStatistics.SearchTime >= TimeSpan.Zero);
            Assert.Equal(0, report.Rules[idle].Matches);
            Assert.Equal(0, report.Rules[idle].IterationsWithMatches);
            Assert.Equal("copy: 3 matches in 1 iterations, " + copyStatistics.SearchTime.TotalMilliseconds.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) + " ms", copyStatistics.ToString());
        }

        [Fact]
        public void TheReportSumsTheEnginesTimes()
        {
            var t = new NodeTheory();
            t.Rules.DefineRule("merge literals", r =>
            {
                var x = r.Var<long>("x");
                r.When(t.Num.Of(x));
                r.Then(t.Lit.Of(x).Is(t.A.Of()));
            });
            t.Root.Assume(t.Num.Of(1L), t.Num.Of(2L));

            var withUnions = t.Root.Run(Schedule.Saturate(t.Rules));

            Assert.True(withUnions.SearchTime > TimeSpan.Zero);
            Assert.True(withUnions.MergeTime > TimeSpan.Zero);
            Assert.True(withUnions.RebuildTime > TimeSpan.Zero);
            Assert.True(withUnions.Elapsed >= withUnions.SearchTime);
            Assert.True(t.Root.AreEqual(t.Root.Get(t.Lit.Of(1L)), t.Root.Get(t.Lit.Of(2L))));

            var copied = t.Graph.DefineRelation<long>("copied");
            var plain = t.Graph.DefineRuleset("plain");
            plain.DefineRule("copy", r =>
            {
                var x = r.Var<long>("x");
                r.When(t.Num.Of(x));
                r.Then(copied.Of(x));
            });
            var withoutUnions = t.Root.Run(Schedule.Saturate(plain));
            Assert.Equal(TimeSpan.Zero, withoutUnions.RebuildTime);
            Assert.True(withoutUnions.MergeTime > TimeSpan.Zero);
        }

        [Fact]
        public void TheReportRendersItsOutcome()
        {
            var t = new NodeTheory();
            var report = t.Root.Run(t.Rules);
            Assert.StartsWith("Completed after 0 iterations, saturated, 0 rules, ", report.ToString());
        }

    }

}

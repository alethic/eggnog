using System;
using System.Threading;

using Eggnog.Tests.Support;

using Xunit;

namespace Eggnog.Tests.Scheduling
{

    /// <summary>
    /// Schedules and run limits (§6.5, D42) at the root.
    /// </summary>
    public sealed class ScheduleTests
    {

        /// <summary>
        /// A theory whose rule counts <c>num</c> up from 0 to <paramref name="limit"/>, one number per iteration.
        /// </summary>
        static NodeTheory Counter(long limit)
        {
            var t = new NodeTheory();
            t.Rules.DefineRule("count", r =>
            {
                var x = r.Var<long>("x");
                r.When(t.Num.Of(x));
                r.Where(Primitives.Less.Of(x, limit));
                r.Then(t.Num.Of(Primitives.Add.Of(x, 1L)));
            });
            t.Root.Assume(t.Num.Of(0L));
            return t;
        }

        [Fact]
        public void RepeatStopsAfterAPassWithNoChange()
        {
            var t = Counter(3);
            var report = t.Root.Run(Schedule.Repeat(100, t.Rules));
            Assert.Equal(4, report.Iterations);
            Assert.True(report.Saturated);
            Assert.True(report.Changed);
            Assert.Equal(RunStopReason.Completed, report.StopReason);
            Assert.True(t.Root.Check(t.Num.Of(3L)));
        }

        [Fact]
        public void RepeatRunsAtMostItsCount()
        {
            var t = Counter(10);
            var report = t.Root.Run(Schedule.Repeat(2, t.Rules));
            Assert.Equal(2, report.Iterations);
            Assert.False(report.Saturated);
            Assert.True(t.Root.Check(t.Num.Of(2L)));
            Assert.False(t.Root.Check(t.Num.Of(3L)));
            Assert.Equal(0, t.Root.Run(Schedule.Repeat(0, t.Rules)).Iterations);
        }

        [Fact]
        public void SaturateRunsUntilNothingChanges()
        {
            var t = Counter(10);
            var report = t.Root.Run(Schedule.Saturate(t.Rules));
            Assert.Equal(11, report.Iterations);
            Assert.True(report.Saturated);
            Assert.True(t.Root.Check(t.Num.Of(10L)));

            var again = t.Root.Run(Schedule.Saturate(t.Rules));
            Assert.Equal(1, again.Iterations);
            Assert.True(again.Saturated);
            Assert.False(again.Changed);
        }

        [Fact]
        public void SequenceRunsEachStepOnce()
        {
            var t = Counter(10);
            var other = t.Graph.DefineRuleset("other");
            var report = t.Root.Run(Schedule.Sequence(t.Rules, other, t.Rules));
            Assert.Equal(2, report.Iterations);
            Assert.True(t.Root.Check(t.Num.Of(2L)));
            Assert.False(report.Saturated);
        }

        [Fact]
        public void ARunStepOverSeveralRulesetsIsOneIteration()
        {
            var t = Counter(10);
            var tagged = t.Graph.DefineRelation<long>("tagged");
            var other = t.Graph.DefineRuleset("other");
            other.DefineRule("tag", r =>
            {
                var x = r.Var<long>("x");
                r.When(t.Num.Of(x));
                r.Then(tagged.Of(x));
            });

            var report = t.Root.Run(Schedule.Run(t.Rules, other, t.Rules));

            Assert.Equal(1, report.Iterations);
            Assert.Equal(2, report.Rules.Count);
            Assert.True(t.Root.Check(t.Num.Of(1L), tagged.Of(0L)));
            Assert.False(t.Root.Check(tagged.Of(1L)));
        }

        [Fact]
        public void TheIterationLimitStopsARun()
        {
            var t = Counter(1000);
            var report = t.Root.Run(Schedule.Saturate(t.Rules), new RunOptions { IterationLimit = 5 });
            Assert.Equal(RunStopReason.IterationLimit, report.StopReason);
            Assert.Equal(5, report.Iterations);
            Assert.False(report.Saturated);
            Assert.True(report.Changed);
            Assert.True(t.Root.Check(t.Num.Of(5L)));
            Assert.False(t.Root.Check(t.Num.Of(6L)));
            Assert.Throws<ArgumentOutOfRangeException>(() => t.Root.Run(t.Rules, new RunOptions { IterationLimit = -1 }));
        }

        [Fact]
        public void CancellationStopsARunBetweenIterationsWithoutFaulting()
        {
            var t = Counter(1000);
            using (var cancelled = new CancellationTokenSource())
            {
                cancelled.Cancel();
                Assert.Throws<OperationCanceledException>(() => t.Root.Run(Schedule.Saturate(t.Rules), cancellationToken: cancelled.Token));
                Assert.False(t.Root.Check(t.Num.Of(1L)));
            }

            using var source = new CancellationTokenSource();
            var stopper = t.Graph.DefineRuleset("stopper");
            stopper.DefineRule("cancel at 3", r =>
            {
                var x = r.Var<long>("x");
                r.When(t.Num.Of(x));
                r.Where(x, v =>
                {
                    if (v == 3)
                        source.Cancel();

                    return false;
                });
            });

            Assert.ThrowsAny<OperationCanceledException>(() => t.Root.Run(Schedule.Saturate(t.Rules, stopper), cancellationToken: source.Token));
            Assert.False(t.Root.IsFaulted);
            Assert.True(t.Root.Check(t.Num.Of(3L)));
            Assert.False(t.Root.Check(t.Num.Of(5L)));
        }

        [Fact]
        public void StopOnContradictionStopsTheTopLevelRun()
        {
            var t = Counter(10);
            t.Rules.DefineRule("contradiction at 2", r =>
            {
                var x = r.Var<long>("x");
                r.When(t.Num.Of(x), x.Is(2L));
                r.Then(t.Graph.Contradiction.Of());
            });

            var stopped = t.Root.Run(Schedule.Saturate(t.Rules), new RunOptions { StopOnContradiction = true });

            Assert.Equal(RunStopReason.Contradiction, stopped.StopReason);
            Assert.False(stopped.Saturated);
            Assert.True(t.Root.IsContradictory);
            Assert.False(t.Root.Check(t.Num.Of(5L)));

            var carriedOn = t.Root.Run(Schedule.Saturate(t.Rules));
            Assert.Equal(RunStopReason.Completed, carriedOn.StopReason);
            Assert.True(carriedOn.Saturated);
            Assert.True(t.Root.Check(t.Num.Of(10L)));

            var refused = t.Root.Run(t.Rules, new RunOptions { StopOnContradiction = true });
            Assert.Equal(RunStopReason.Contradiction, refused.StopReason);
            Assert.Equal(0, refused.Iterations);
        }

        [Fact]
        public void AnEmptyRulesetDoesNotIterate()
        {
            var t = new NodeTheory();
            var report = t.Root.Run(Schedule.Saturate(t.Rules));
            Assert.Equal(0, report.Iterations);
            Assert.True(report.Saturated);
            Assert.False(report.Changed);
            Assert.Empty(report.Rules);
            Assert.Equal(0, t.Root.Run(Schedule.Run()).Iterations);
        }

        [Fact]
        public void ARulesetOfAnotherGraphIsRefused()
        {
            var t = new NodeTheory();
            var other = new NodeTheory();
            Assert.Throws<ArgumentException>(() => t.Root.Run(other.Rules));
            Assert.Throws<ArgumentException>(() => t.Root.Run(Schedule.Saturate(t.Rules, Schedule.Sequence(other.Rules))));
            Assert.False(t.Root.IsFaulted);
        }

        [Fact]
        public void SchedulesRejectNullsAndNegativeCounts()
        {
            var t = new NodeTheory();
            Assert.Throws<ArgumentOutOfRangeException>(() => Schedule.Repeat(-1, t.Rules));
            Assert.Throws<ArgumentNullException>(() => Schedule.Sequence(null!));
            Assert.Throws<ArgumentNullException>(() => Schedule.Saturate(t.Rules, null!));
            Assert.Throws<ArgumentNullException>(() => t.Root.Run(null!));
        }

        [Fact]
        public void UntilRunsTheBodyUnlessTheConditionHolds()
        {
            var t = Counter(10);
            var report = t.Root.Run(Schedule.Saturate(Schedule.Until(t.Rules, t.Num.Of(4L))));
            Assert.Equal(4, report.Iterations);
            Assert.Equal(RunStopReason.Completed, report.StopReason);
            Assert.True(t.Root.Check(t.Num.Of(4L)));
            Assert.False(t.Root.Check(t.Num.Of(5L)));

            // The condition holds already: nothing runs.
            Assert.Equal(0, t.Root.Run(Schedule.Until(t.Rules, t.Num.Of(2L))).Iterations);

            // Every fact must hold: num(4) does, num(6) does not yet.
            var both = t.Root.Run(Schedule.Saturate(Schedule.Until(t.Rules, t.Num.Of(4L), t.Num.Of(6L))));
            Assert.Equal(2, both.Iterations);
            Assert.True(t.Root.Check(t.Num.Of(6L)));
            Assert.False(t.Root.Check(t.Num.Of(7L)));
        }

        [Fact]
        public void UntilChecksTheConditionBeforeEveryPass()
        {
            var t = Counter(10);
            var other = t.Graph.DefineRuleset("other");
            var report = t.Root.Run(Schedule.Repeat(3, Schedule.Until(Schedule.Sequence(t.Rules, t.Rules), t.Num.Of(3L))));
            Assert.Equal(4, report.Iterations);
            Assert.True(t.Root.Check(t.Num.Of(4L)));
            Assert.False(t.Root.Check(t.Num.Of(5L)));
            Assert.Equal(0, t.Root.Run(Schedule.Until(other, t.Num.Of(99L))).Iterations);
        }

        [Fact]
        public void UntilRefusesFiltersAndNulls()
        {
            var t = Counter(10);
            Assert.Throws<ArgumentException>(() => Schedule.Until(t.Rules, ((Term<long>)1L).IsNot(2L)));
            Assert.Throws<ArgumentException>(() => Schedule.Until(t.Rules, Fact.True(Primitives.Less.Of(1L, 2L))));
            Assert.Throws<ArgumentNullException>(() => Schedule.Until(null!, t.Num.Of(1L)));
            Assert.Throws<ArgumentNullException>(() => Schedule.Until(t.Rules, null!));
            Assert.Throws<ArgumentNullException>(() => Schedule.Until(t.Rules, t.Num.Of(1L), null!));
        }

        [Fact]
        public void UntilRefusesAnEmptyConditionAndTermsAHostCheckCannotRead()
        {
            var t = Counter(10);
            var empty = Assert.Throws<ArgumentException>(() => Schedule.Until(t.Rules));
            Assert.StartsWith("An until condition needs at least one fact.", empty.Message, StringComparison.Ordinal);

            Var<long> leaked = null!;
            t.Graph.DefineQuery("leak", q =>
            {
                leaked = q.Var<long>("v");
                q.When(t.Num.Of(leaked));
            });
            var variable = Assert.Throws<ArgumentException>(() => Schedule.Until(t.Rules, t.Num.Of(leaked)));
            Assert.Contains("'v' is a variable", variable.Message, StringComparison.Ordinal);
            var computation = Assert.Throws<ArgumentException>(() => Schedule.Until(t.Rules, t.Num.Of(Primitives.Add.Of(1L, 1L))));
            Assert.Contains("'+' is a computation", computation.Message, StringComparison.Ordinal);
            Assert.Throws<ArgumentException>(() => Schedule.Until(t.Rules, Fact.Exists(t.Wrap.Of(t.Lit.Of(Primitives.Max.Of(1L, 2L))))));
        }

        [Fact]
        public void RunRefusesAnUntilConditionOfAnotherGraphBeforeAnyWork()
        {
            var t = Counter(10);
            var other = new NodeTheory();
            var foreignClass = other.Root.Make(other.A.Of());
            var version = t.Root.Version;

            // The first step would count; each condition is refused before it runs.
            foreach (var condition in new[] { other.Num.Of(3L), t.Wrap.Of(foreignClass).Is(t.Wrap.Of(t.A.Of())), t.Wrap.Of(default(Node)).Is(t.A.Of()) })
            {
                var error = Assert.Throws<ArgumentException>(() => t.Root.Run(Schedule.Sequence(t.Rules, Schedule.Until(t.Rules, condition))));
                Assert.StartsWith("An until condition cannot be checked in scope 'root'", error.Message, StringComparison.Ordinal);
                Assert.Equal(version, t.Root.Version);
                Assert.False(t.Root.Check(t.Num.Of(1L)));
            }

            Assert.False(t.Root.IsFaulted);
            Assert.Equal(RunStopReason.Completed, t.Root.Run(Schedule.Sequence(t.Rules, Schedule.Until(t.Rules, t.Num.Of(3L)))).StopReason);
            Assert.True(t.Root.Check(t.Num.Of(2L)));
        }

        [Fact]
        public void TheTimeLimitOvershootsByAtMostOneIteration()
        {
            var t = new NodeTheory();
            var pause = TimeSpan.FromMilliseconds(40);
            t.Rules.DefineRule("slow count", r =>
            {
                var x = r.Var<long>("x");
                r.When(t.Num.Of(x));
                r.Where(x, v =>
                {
                    Thread.Sleep(pause);
                    return v < 100_000;
                });
                r.Then(t.Num.Of(Primitives.Add.Of(x, 1L)));
            });
            t.Root.Assume(t.Num.Of(0L));
            var limit = TimeSpan.FromMilliseconds(300);

            var report = t.Root.Run(Schedule.Saturate(t.Rules), new RunOptions { TimeLimit = limit, RecordSteps = true });

            Assert.Equal(RunStopReason.TimeLimit, report.StopReason);
            Assert.False(report.Saturated);
            Assert.True(report.Iterations >= 1);
            Assert.True(report.Elapsed > limit);

            // The last iteration began before the limit passed: the ones before it fit in the limit.
            var beforeLast = TimeSpan.Zero;
            for (var i = 0; i < report.Steps.Count - 1; i++)
                beforeLast += report.Steps[i].Elapsed;

            Assert.True(beforeLast <= limit, $"{beforeLast} of iterations before the last one");
            Assert.False(t.Root.IsFaulted);

            // A limit of zero has passed by the first check, or by the second.
            var zero = t.Root.Run(Schedule.Saturate(t.Rules), new RunOptions { TimeLimit = TimeSpan.Zero });
            Assert.Equal(RunStopReason.TimeLimit, zero.StopReason);
            Assert.InRange(zero.Iterations, 0, 1);
        }

        [Fact]
        public void TheRowLimitStopsAGrowingProgram()
        {
            var t = Counter(1_000_000);
            var unlimited = t.Root.Run(Schedule.Repeat(3, t.Rules));
            Assert.Equal(3, unlimited.Iterations);

            var start = t.Graph.Engine.Database.TotalSizeEstimate;
            var limit = start + 20;
            var report = t.Root.Run(Schedule.Saturate(t.Rules), new RunOptions { RowLimit = limit });

            Assert.Equal(RunStopReason.RowLimit, report.StopReason);
            Assert.False(report.Saturated);

            // Each iteration adds one row, and the run stops at the first check that sees more than the limit.
            Assert.Equal(limit + 1, t.Graph.Engine.Database.TotalSizeEstimate);
            Assert.Equal(21, report.Iterations);
            Assert.Equal(25, t.Root.CountRows(t.Num));
            Assert.False(t.Root.IsFaulted);
        }

        [Fact]
        public void NegativeLimitsAreRefused()
        {
            var t = Counter(10);
            Assert.Throws<ArgumentOutOfRangeException>(() => t.Root.Run(t.Rules, new RunOptions { TimeLimit = TimeSpan.FromSeconds(-1) }));
            Assert.Throws<ArgumentOutOfRangeException>(() => t.Root.Run(t.Rules, new RunOptions { RowLimit = -1 }));
            Assert.False(t.Root.Check(t.Num.Of(1L)));
            Assert.Equal(RunStopReason.RowLimit, t.Root.Run(t.Rules, new RunOptions { RowLimit = 0 }).StopReason);
        }

    }

}

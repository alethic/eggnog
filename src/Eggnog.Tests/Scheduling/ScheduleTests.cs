using System;
using System.Linq;
using System.Threading;

using Eggnog.Tests.Support;

using Xunit;

namespace Eggnog.Tests.Scheduling
{

    /// <summary>
    /// Schedules and run limits at the root: <c>Repeat</c>, <c>Saturate</c>, <c>Sequence</c> and <c>Until</c>; the
    /// iteration, time and row limits, which are checked before the first iteration and between iterations; and the
    /// refusal, before any work, of what a run cannot execute.
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

        /// <summary>
        /// The iteration limit counts the iterations of the run's cases, and the row limit their own rows.
        /// </summary>
        [Fact]
        public void LimitsCountCaseIterationsAndCaseRows()
        {
            static (Cosmos.Scoped.CosmosTheory C, Scope Scope) Split()
            {
                var c = new Cosmos.Scoped.CosmosTheory(new EGraph());
                c.Graph.Root.Freeze();
                var scope = c.Graph.Root.CreateChild(c.Schema);
                scope.Assume(c.Present.Of("kind"), c.Dom.Of("kind").Is(ValueSet.Of("a", "b", "c")), c.Discriminator.Of("kind"));
                scope.Run(Schedule.Saturate(c.Horn, c.Lattice));
                return (c, scope);
            }

            // Unlimited: every iteration of the cases counts.
            var (c, scope) = Split();
            var full = scope.Run(Schedule.Split(c.SchemaSplit, Schedule.Saturate(c.Horn, c.Lattice)), new RunOptions { RecordSteps = true });
            var caseIterations = full.Steps.Count(s => s.Kind == RunStepKind.Iteration && s.Scope.IsCase);
            Assert.True(caseIterations >= 3);
            Assert.Equal(full.Steps.Count(s => s.Kind == RunStepKind.Iteration), full.Iterations);
            Assert.Equal(caseIterations, full.Iterations);              // the scope itself had nothing left to do

            // A limit below that stops inside the cases, and the cases are disposed.
            (c, scope) = Split();
            var limited = scope.Run(Schedule.Split(c.SchemaSplit, Schedule.Saturate(c.Horn, c.Lattice)), new RunOptions { IterationLimit = 2 });
            Assert.Equal(RunStopReason.IterationLimit, limited.StopReason);
            Assert.Equal(2, limited.Iterations);
            Assert.Equal(3, limited.CasesOpened);
            Assert.Empty(scope.Children);

            // The row limit: the scope's own rows fit, but with the rows each case assumed they do not.
            (c, scope) = Split();
            var rows = scope.Engine.OwnRowCount;
            var bounded = scope.Run(Schedule.Split(c.SchemaSplit, Schedule.Saturate(c.Horn, c.Lattice)), new RunOptions { RowLimit = rows });
            Assert.Equal(RunStopReason.RowLimit, bounded.StopReason);
            Assert.Equal(3, bounded.CasesOpened);
            Assert.Equal(0, bounded.Iterations);
            Assert.Empty(scope.Children);
        }

        /// <summary>
        /// The row limit is checked before the first iteration too.
        /// </summary>
        [Fact]
        public void TheRowLimitIsCheckedBeforeTheFirstIteration()
        {
            var t = new NodeTheory();
            var facts = new Fact[100];
            for (var i = 0; i < facts.Length; i++)
                facts[i] = t.Num.Of((long)i);

            t.Root.Assume(facts);
            t.Rules.DefineRule("copy", r =>
            {
                var x = r.Var<long>("x");
                r.When(t.Num.Of(x));
                r.Then(t.Tag.Of(t.Lit.Of(x)));
            });

            var report = t.Root.Run(Schedule.Saturate(t.Rules), new RunOptions { RowLimit = 10 });

            Assert.Equal(RunStopReason.RowLimit, report.StopReason);
            Assert.Equal(0, report.Iterations);
        }

        [Fact]
        public void RowLimitStopsBeforeTheFirstIteration()
        {
            var t = new NodeTheory();
            var facts = Enumerable.Range(0, 10).Select(i => t.Num.Of((long)i)).ToArray();
            t.Root.Assume(facts);
            t.Rules.DefineRule("next", r =>
            {
                var x = r.Var<long>("x");
                r.When(t.Num.Of(x));
                r.Then(t.Num.Of(Primitives.Add.Of(x, 1L)));
            });
            var report = t.Root.Run(Schedule.Saturate(t.Rules), new RunOptions { RowLimit = 5 });
            Assert.Equal(RunStopReason.RowLimit, report.StopReason);
            Assert.Equal(0, report.Iterations);
        }

        [Fact]
        public void TimeLimitZeroStopsBeforeTheFirstIteration()
        {
            var t = new NodeTheory();
            t.Root.Assume(t.Num.Of(1L));
            t.Rules.DefineRule("next", r =>
            {
                var x = r.Var<long>("x");
                r.When(t.Num.Of(x));
                r.Then(t.Num.Of(Primitives.Add.Of(x, 1L)));
            });
            var report = t.Root.Run(Schedule.Saturate(t.Rules), new RunOptions { TimeLimit = TimeSpan.Zero });
            Assert.Equal(RunStopReason.TimeLimit, report.StopReason);
            Assert.Equal(0, report.Iterations);
            Assert.False(report.Saturated);
        }

        [Fact]
        public void UntilStopsWhenTheConditionHolds()
        {
            var t = new NodeTheory();
            t.Root.Assume(t.Num.Of(1L));
            t.Rules.DefineRule("next", r =>
            {
                var x = r.Var<long>("x");
                r.When(t.Num.Of(x));
                r.Where(Primitives.Less.Of(x, 100L));
                r.Then(t.Num.Of(Primitives.Add.Of(x, 1L)));
            });
            var report = t.Root.Run(Schedule.Saturate(Schedule.Until(t.Rules, t.Num.Of(5L))));
            Assert.True(t.Root.Check(t.Num.Of(5L)));
            Assert.False(t.Root.Check(t.Num.Of(6L)));
            Assert.Equal(4, report.Iterations);

            var none = t.Root.Run(Schedule.Until(t.Rules, t.Num.Of(5L)));
            Assert.Equal(0, none.Iterations);
            Assert.False(none.Changed);
        }

        [Fact]
        public void LimitPrecedence()
        {
            var t = new NodeTheory();
            t.Root.Assume(t.Num.Of(1L));
            t.Rules.DefineRule("next", r =>
            {
                var x = r.Var<long>("x");
                r.When(t.Num.Of(x));
                r.Then(t.Num.Of(Primitives.Add.Of(x, 1L)));
            });
            Assert.Equal(RunStopReason.IterationLimit, t.Root.Run(t.Rules, new RunOptions { IterationLimit = 0, TimeLimit = TimeSpan.Zero, RowLimit = 0 }).StopReason);
            Assert.Equal(RunStopReason.TimeLimit, t.Root.Run(t.Rules, new RunOptions { TimeLimit = TimeSpan.Zero, RowLimit = 0 }).StopReason);
            Assert.Equal(RunStopReason.RowLimit, t.Root.Run(t.Rules, new RunOptions { RowLimit = 0 }).StopReason);
            using var cts = new System.Threading.CancellationTokenSource();
            cts.Cancel();
            Assert.ThrowsAny<OperationCanceledException>(() => t.Root.Run(t.Rules, new RunOptions { IterationLimit = 0 }, cts.Token));
        }

        [Fact]
        public void AnotherGraphsHandleInAnUntilCondition()
        {
            var t = new NodeTheory();
            var other = new NodeTheory();
            other.Root.Assume(other.Num.Of(2L));
            t.Root.Assume(t.Num.Of(1L));
            t.Rules.DefineRule("next", r =>
            {
                var x = r.Var<long>("x");
                r.When(t.Num.Of(x));
                r.Where(Primitives.Less.Of(x, 5L));
                r.Then(t.Num.Of(Primitives.Add.Of(x, 1L)));
            });
            Assert.Throws<ArgumentException>(() => t.Root.Check(other.Num.Of(2L)));
            Assert.Throws<ArgumentException>(() => t.Root.Run(Schedule.Saturate(Schedule.Until(t.Rules, other.Num.Of(2L)))));
        }

        /// <summary>
        /// <c>Schedule.Until</c> refuses filters at construction; a condition no host check of the scope can read (a
        /// computation, or another graph's table) is refused by <c>Run</c> before any work, as another graph's ruleset
        /// is: the earlier steps do not iterate, and the version does not move.
        /// </summary>
        /// <param name="foreignTable">Whether the condition reads another graph's table rather than a
        /// computation.</param>
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void AnUncheckableUntilConditionIsRefusedBeforeAnyWork(bool foreignTable)
        {
            var t = new NodeTheory();
            var other = new NodeTheory();
            t.Root.Assume(t.Num.Of(1L));
            t.Rules.DefineRule("next", r =>
            {
                var x = r.Var<long>("x");
                r.When(t.Num.Of(x));
                r.Where(Primitives.Less.Of(x, 3L));
                r.Then(t.Num.Of(Primitives.Add.Of(x, 1L)));
            });
            var version = t.Root.Version;
            var condition = foreignTable ? other.Num.Of(2L) : t.Num.Of(Primitives.Add.Of(1L, 1L));

            var error = Record.Exception(() => t.Root.Run(Schedule.Sequence(t.Rules, Schedule.Until(t.Rules, condition))));

            Assert.IsType<ArgumentException>(error);
            Assert.Equal(version, t.Root.Version);
            Assert.False(t.Root.Check(t.Num.Of(2L)));
        }

        /// <summary>
        /// A naive rule that re-inserts a row that already exists changes nothing, so <c>Saturate</c> ends.
        /// </summary>
        [Fact]
        public void ANaiveRuleReinsertingAnExistingRowSaturates()
        {
            var t = new NodeTheory();
            t.Rules.DefineRule("always", r =>
            {
                r.Naive();
                r.Then(t.Num.Of(7L));
            });
            var report = t.Root.Run(Schedule.Saturate(t.Rules), new RunOptions { IterationLimit = 50 });
            Assert.Equal(RunStopReason.Completed, report.StopReason);
            Assert.True(report.Saturated);
            Assert.True(report.Iterations <= 3, $"{report.Iterations} iterations");
        }

        /// <summary>
        /// A naive rule writing a lattice value the stored one absorbs changes nothing, so <c>Saturate</c> ends.
        /// </summary>
        [Fact]
        public void ANaiveLatticeWriteThatIsAbsorbedSaturates()
        {
            var t = new NodeTheory();
            var lo = t.Graph.DefineFunction<long, long>("lo", ValueLattice.Max<long>());
            t.Root.Assume(lo.Of(1L).Is(5L));
            t.Rules.DefineRule("lower bound", r =>
            {
                r.Naive();
                r.Then(lo.Of(1L).Is(3L));
            });
            var report = t.Root.Run(Schedule.Saturate(t.Rules), new RunOptions { IterationLimit = 50 });
            Assert.Equal(RunStopReason.Completed, report.StopReason);
            Assert.True(report.Iterations <= 3, $"{report.Iterations} iterations");
            Assert.Equal(5L, t.Root.Get(lo.Of(1L)));
        }

        /// <summary>
        /// <c>Schedule.Run</c> with no ruleset is allowed and does nothing; a <c>Saturate</c> of it ends without an
        /// iteration.
        /// </summary>
        [Fact]
        public void AScheduleWithAnEmptyRunEnds()
        {
            var t = new NodeTheory();
            var report = t.Root.Run(Schedule.Saturate(Schedule.Run()));
            Assert.Equal(0, report.Iterations);
            Assert.Equal(RunStopReason.Completed, report.StopReason);
        }

    }

}

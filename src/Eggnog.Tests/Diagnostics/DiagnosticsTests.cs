using System;
using System.Collections.Generic;
using System.Linq;

using Eggnog.Tests.Cosmos.Literal;
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

        /// <summary>
        /// A theory whose rule counts <c>num</c> up from 0 to 3, one number per iteration, and copies each into
        /// <c>copied</c>.
        /// </summary>
        static (NodeTheory T, Rule Count, Rule Copy, Relation<long> Copied) Counting(EGraphOptions? options = null)
        {
            var t = new NodeTheory(options);
            var copied = t.Graph.DefineRelation<long>("copied");
            var count = t.Rules.DefineRule("count", r =>
            {
                var x = r.Var<long>("x");
                r.When(t.Num.Of(x));
                r.Where(Primitives.Less.Of(x, 3L));
                r.Then(t.Num.Of(Primitives.Add.Of(x, 1L)));
            });
            var copy = t.Rules.DefineRule("copy", r =>
            {
                var x = r.Var<long>("x");
                r.When(t.Num.Of(x));
                r.Then(copied.Of(x));
            });
            t.Root.Assume(t.Num.Of(0L));
            return (t, count, copy, copied);
        }

        [Fact]
        public void PlansAreRecordedWithRecordPlans()
        {
            var (plain, plainCount, _, _) = Counting();
            var withoutPlans = plain.Root.Run(Schedule.Saturate(plain.Rules));
            Assert.Null(withoutPlans.Rules[plainCount].Plan);

            var (t, count, copy, _) = Counting(new EGraphOptions { RecordPlans = true });
            var report = t.Root.Run(Schedule.Saturate(t.Rules));
            Assert.False(string.IsNullOrWhiteSpace(report.Rules[count].Plan));
            Assert.False(string.IsNullOrWhiteSpace(report.Rules[copy].Plan));
            Assert.True(new EGraphOptions { RecordPlans = true }.RecordPlans);
            Assert.False(EGraphOptions.Default.RecordPlans);
        }

        [Fact]
        public void RecordStepsListsEveryIterationInOrder()
        {
            var (t, count, copy, _) = Counting();

            var report = t.Root.Run(Schedule.Saturate(t.Rules), new RunOptions { RecordSteps = true });

            Assert.Equal(5, report.Iterations);
            Assert.Equal(report.Iterations, report.Steps.Count);
            for (var i = 0; i < report.Steps.Count; i++)
            {
                var step = report.Steps[i];
                Assert.Equal(i, step.Index);
                Assert.Equal(RunStepKind.Iteration, step.Kind);
                Assert.Same(t.Root, step.Scope);
                Assert.Equal("root", step.ScopePath);
                Assert.True(step.Elapsed >= TimeSpan.Zero);
            }

            // Iterations 0 to 3 add num(1) to num(3) and their copies; the last one only copies num(3)... and then nothing.
            Assert.Equal(new[] { true, true, true, true, false }, Array.ConvertAll(report.Steps.ToArray(), s => s.Changed));
            Assert.Equal(new[] { count, copy }, Array.ConvertAll(report.Steps[0].Rules.ToArray(), r => r.Rule));
            Assert.Equal(new[] { 1L, 1L }, Array.ConvertAll(report.Steps[0].Rules.ToArray(), r => r.Matches));
            Assert.Equal(new[] { copy }, Array.ConvertAll(report.Steps[3].Rules.ToArray(), r => r.Rule));
            Assert.Empty(report.Steps[4].Rules);
            Assert.Equal(1, report.Steps[0].Rules[0].IterationsWithMatches);
            Assert.Equal("#0 iteration root changed: count 1, copy 1", report.Steps[0].ToString());
            Assert.Equal("#4 iteration root unchanged", report.Steps[4].ToString());

            Assert.Empty(t.Root.Run(Schedule.Saturate(t.Rules)).Steps);
        }

        [Fact]
        public void TheObserverSeesTheSameStepsAndMayOnlyRead()
        {
            var (t, _, _, copied) = Counting();
            var seen = new List<RunStep>();
            var counts = new List<long>();
            var checks = new List<bool>();
            var refusals = new List<Exception>();
            var options = new RunOptions
            {
                RecordSteps = true,
                Observer = step =>
                {
                    seen.Add(step);
                    checks.Add(step.Scope.Check(t.Num.Of(1L)));
                    counts.Add(step.Scope.CountRows(copied));
                    refusals.Add(Assert.Throws<InvalidOperationException>(() => step.Scope.Assume(t.Num.Of(100L))));
                    refusals.Add(Assert.Throws<InvalidOperationException>(() => step.Scope.Run(t.Rules)));
                    refusals.Add(Assert.Throws<InvalidOperationException>(() => step.Scope.Make(t.A.Of())));
                },
            };

            var report = t.Root.Run(Schedule.Saturate(t.Rules), options);

            Assert.Equal(report.Steps, seen);
            Assert.Equal(new[] { true, true, true, true, true }, checks);
            Assert.Equal(new[] { 1L, 2L, 3L, 4L, 4L }, counts);
            Assert.All(refusals, e => Assert.Equal("Not allowed inside a run observer", e.Message));
            Assert.False(t.Root.Check(t.Num.Of(100L)));
            Assert.False(t.Root.IsFaulted);

            // After the run the scope is writable again.
            t.Root.Assume(t.Num.Of(100L));
            Assert.True(t.Root.Check(t.Num.Of(100L)));
        }

        [Fact]
        public void AnObserverWithoutRecordStepsStillSeesEveryStepAndMayQuery()
        {
            var (t, _, _, copied) = Counting();
            var copies = t.Graph.DefineQuery("copies", q =>
            {
                var x = q.Var<long>("x");
                q.When(copied.Of(x));
            });
            var indexes = new List<int>();
            var matches = new List<long>();
            var report = t.Root.Run(Schedule.Saturate(t.Rules), new RunOptions
            {
                Observer = step =>
                {
                    indexes.Add(step.Index);
                    matches.Add(step.Scope.Count(copies));
                },
            });

            Assert.Empty(report.Steps);
            Assert.Equal(new[] { 0, 1, 2, 3, 4 }, indexes);
            Assert.Equal(new[] { 1L, 2L, 3L, 4L, 4L }, matches);
        }

        [Fact]
        public void AnObserverExceptionPropagatesWithoutFaulting()
        {
            var (t, _, _, _) = Counting();
            var error = Assert.Throws<InvalidOperationException>(() => t.Root.Run(Schedule.Saturate(t.Rules), new RunOptions
            {
                Observer = step => throw new InvalidOperationException("observer failed at " + step.Index),
            }));

            Assert.Equal("observer failed at 0", error.Message);
            Assert.False(t.Root.IsFaulted);
            Assert.True(t.Root.Check(t.Num.Of(1L)));
            t.Root.Assume(t.Num.Of(50L));
            Assert.Equal(RunStopReason.Completed, t.Root.Run(Schedule.Saturate(t.Rules)).StopReason);
        }

        [Fact]
        public void CountRowsAndRowsMatchThePrototypesPrintSizeOnLiteral07()
        {
            var c = Literal07SweepLinksTests.Container();
            Literal07SweepLinksTests.Ask(c, Literal07SweepLinksTests.OfflineRefresh(c,
                c.UuidEq.Of("data.data.parkId", Literal07SweepLinksTests.Park),
                c.InstantGt.Of("data.metadata.changeUtcTime", Literal07SweepLinksTests.Since)));
            var root = c.Graph.Root;

            // (print-size Assume) 6, (print-size parent) 7, (print-size form) 40.
            Assert.Equal(6, root.CountRows(c.Assume));
            Assert.Equal(7, root.CountRows(c.Parent));
            Assert.Equal(40, root.CountRows(c.Form));
            Assert.Equal(6, root.Rows(c.Assume).Count);
            Assert.Equal(7, root.Rows(c.Parent).Count);
            Assert.Equal(40, root.Rows(c.Form).Count);

            // Three cases under (Schema) and three under (Query), one per value of data.type.
            var schema = root.Get(c.Schema.Of());
            var query = root.Get(c.Query.Of());
            var cases = root.Rows(c.Assume);
            Assert.All(cases, row => Assert.Equal("data.type", row.Keys[1]));
            Assert.Equal(3, cases.Count(row => root.AreEqual(Ctx.FromId((ClassId)row.Keys[0]), schema)));
            Assert.Equal(3, cases.Count(row => root.AreEqual(Ctx.FromId((ClassId)row.Keys[0]), query)));
            Assert.Equal(new[] { "map", "park", "spot" }, cases.Where(row => root.AreEqual(Ctx.FromId((ClassId)row.Keys[0]), schema)).Select(row => (string)row.Keys[2]));
        }

        [Fact]
        public void RowsRenderAsSExpressions()
        {
            var c = Literal07SweepLinksTests.Container();
            var root = c.Graph.Root;
            var schema = root.Get(c.Schema.Of());

            Assert.Equal(
                new[] { "(discriminator \"data.type\" \"map\")", "(discriminator \"data.type\" \"park\")", "(discriminator \"data.type\" \"spot\")" },
                root.Rows(c.Discriminator).Select(r => r.ToString()));
            Assert.Equal($"(dom {schema.Id} \"data.type\") = (set-of \"map\" \"park\" \"spot\")", Assert.Single(root.Rows(c.Dom)).ToString());
            Assert.Equal($"(lo {schema.Id} \"linkId\") = 1", Assert.Single(root.Rows(c.Lo)).ToString());
            Assert.Equal($"(Schema) = {schema.Id}", Assert.Single(root.Rows(c.Schema)).ToString());
            Assert.Contains($"(form {schema.Id} \"data.guid\" \"uuid-lower\")", root.Rows(c.Form).Select(r => r.ToString()));
        }


        [Fact]
        public void MirrorSyncsAreSteps()
        {
            var t = new ScopeTheory();
            var copies = new OuterCopies(t);
            var outer = t.Root.CreateChild(t.Outer);
            outer.Assume(t.Fact.Of("a"), t.Best.Of("k").Is(3L));
            var inner = outer.CreateChild(t.Inner);
            var observed = new List<RunStep>();

            var report = inner.Run(Schedule.Saturate(t.Rules), new RunOptions { RecordSteps = true, Observer = observed.Add });

            // One sync of the outer scope before the first iteration; later iterations find it current.
            var syncs = report.Steps.Where(s => s.Kind == RunStepKind.MirrorSync).ToArray();
            var sync = Assert.Single(syncs);
            Assert.Same(outer, sync.Scope);
            Assert.True(sync.Changed);
            Assert.Empty(sync.Rules);
            Assert.Equal(0, sync.Index);
            Assert.StartsWith("#0 mirror-sync " + outer.Path + " changed", sync.ToString());
            Assert.Equal(report.Steps, observed);
            Assert.Equal(report.Iterations, report.Steps.Count(s => s.Kind == RunStepKind.Iteration));
            Assert.True(inner.Check(copies.Facts.Of("a"), copies.Bests.Of("k").Is(3L)));

            // A write into the outer scope makes the next run sync again; an unchanged scope does not.
            outer.Assume(t.Fact.Of("b"));
            Assert.Single(inner.Run(Schedule.Saturate(t.Rules), new RunOptions { RecordSteps = true }).Steps, s => s.Kind == RunStepKind.MirrorSync);
            Assert.DoesNotContain(inner.Run(Schedule.Saturate(t.Rules), new RunOptions { RecordSteps = true }).Steps, s => s.Kind == RunStepKind.MirrorSync);
        }

        /// <summary>
        /// R3: a split step is a step, recorded after the iterations of its cases and the lifts it made; each lift is a
        /// step of the split scope, recorded between the cases' iterations. The observer sees the same steps and may read a
        /// case while it runs.
        /// </summary>
        [Fact]
        public void SplitStepsAndLiftsAreSteps()
        {
            var c = new Cosmos.Scoped.CosmosTheory(new EGraph());
            c.Graph.Root.Freeze();
            var scope = c.Graph.Root.CreateChild(c.Schema);
            scope.Assume(c.Present.Of("kind"), c.Dom.Of("kind").Is(ValueSet.Of("a", "b")), c.Discriminator.Of("kind"));
            c.DeclareBranch(scope, "kind", "a", c.Form.Of("ref", "uuid-lower"));
            c.DeclareBranch(scope, "kind", "b", c.Form.Of("ref", "uuid-lower"));
            var observed = new List<RunStep>();
            var readInCase = 0;

            var report = scope.Run(Schedule.Saturate(Schedule.Saturate(c.Horn, c.Lattice), Schedule.Split(c.SchemaSplit, Schedule.Saturate(c.Horn, c.Lattice))),
                new RunOptions
                {
                    RecordSteps = true,
                    Observer = step =>
                    {
                        observed.Add(step);
                        if (step.Kind == RunStepKind.Iteration && step.Scope.IsCase && step.Scope.Check(c.Present.Of("kind")))
                            readInCase++;
                    },
                });

            Assert.Equal(report.Steps, observed);
            Assert.True(readInCase > 0);
            var splitSteps = report.Steps.Where(s => s.Kind == RunStepKind.SplitStep).ToArray();
            Assert.True(splitSteps.Length >= 2);                                  // the first opens, a later one finds nothing new
            var first = splitSteps[0];
            Assert.Same(scope, first.Scope);
            Assert.Equal(2, first.CasesOpened);
            Assert.True(first.Changed);
            Assert.Empty(first.Rules);
            Assert.Equal($"#{first.Index} split-step {scope.Path} changed: 2 cases", first.ToString());
            Assert.All(splitSteps.Skip(1), s => Assert.Equal(0, s.CasesOpened));
            Assert.False(splitSteps[^1].Changed);

            var lifts = report.Steps.Where(s => s.Kind == RunStepKind.Lift).ToArray();
            Assert.Equal(report.Lifts, lifts.Length);
            Assert.All(lifts, l => Assert.Same(scope, l.Scope));
            Assert.Contains(lifts, l => l.Changed);
            Assert.StartsWith($"#{lifts[0].Index} lift {scope.Path} changed", lifts[0].ToString(), StringComparison.Ordinal);

            // Within the first split step: the cases' iterations without nested splits (phase A), its lift, the cases'
            // iterations with nested splits (phase B, which changed nothing, so no second lift), then the step itself.
            var caseIterations = report.Steps.Where(s => s.Kind == RunStepKind.Iteration && s.Scope.IsCase && s.Index < first.Index).ToArray();
            Assert.NotEmpty(caseIterations);
            Assert.All(caseIterations, s => Assert.Contains("/case#", s.ScopePath, StringComparison.Ordinal));
            Assert.True(lifts[0].Index > caseIterations[0].Index && lifts[0].Index < first.Index);
            Assert.Contains(caseIterations, s => s.Index > lifts[0].Index);
            Assert.DoesNotContain(lifts, l => l.Index > lifts[0].Index && l.Index < first.Index);
            Assert.All(caseIterations, s => Assert.True(s.Scope.IsDisposed));       // the run ended: its cases are gone
            Assert.Equal(report.Iterations, report.Steps.Count(s => s.Kind == RunStepKind.Iteration));
        }
    }

}

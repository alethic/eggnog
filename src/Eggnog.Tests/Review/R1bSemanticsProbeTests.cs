using System;
using System.Collections.Generic;
using System.Linq;

using Eggnog.Tests.Support;

using Xunit;

namespace Eggnog.Tests.Review
{

    /// <summary>
    /// Probes of R1b's semantics (review): queries, limits, observers, steps, plans and row reads. All pass; they pin behaviour
    /// the review checked (content order of matches and rows, limit precedence, observer refusals, D49).
    /// </summary>
    public sealed class R1bSemanticsProbeTests
    {

        [Fact]
        public void P01_DefiningAQueryKeepsALiveExtractorUsable()
        {
            var t = new NodeTheory();
            var a = t.Root.Make(t.Wrap.Of(t.A.Of()));
            var extractor = t.Root.CreateExtractor();
            var q = t.Graph.DefineQuery("q", b =>
            {
                var x = b.Var<long>("x");
                b.When(t.Num.Of(x));
            });
            Assert.Equal("(Wrap (A))", extractor.Extract(a).ToString());
            Assert.Equal(0, t.Root.Count(q));
            Assert.Equal("(Wrap (A))", extractor.Extract(a).ToString());
            q.Dispose();
            Assert.Equal("(Wrap (A))", extractor.Extract(a).ToString());
        }

        [Fact]
        public void P02_AQueryWithAnEmptyBodyHasOneMatch()
        {
            var t = new NodeTheory();
            var empty = t.Graph.DefineQuery("empty", b => { });
            Assert.True(t.Root.Check());
            Assert.Equal(1, t.Root.Count(empty));
            Assert.True(t.Root.Any(empty));
        }

        [Fact]
        public void P03_AQueryWithOnlyAConstantFilter()
        {
            var t = new NodeTheory();
            var yes = t.Graph.DefineQuery("yes", b => b.Where(Primitives.Greater.Of(2L, 1L)));
            var no = t.Graph.DefineQuery("no", b => b.Where(Primitives.Greater.Of(1L, 2L)));
            Assert.Equal(1, t.Root.Count(yes));
            Assert.Equal(0, t.Root.Count(no));
        }

        [Fact]
        public void P04_RowLimitStopsBeforeTheFirstIteration()
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
        public void P05_TimeLimitZeroStopsBeforeTheFirstIteration()
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
        public void P06_ContentOrderOfNegativeAndLargeNumbers()
        {
            var t = new NodeTheory();
            var values = new long[] { 5, -1, long.MaxValue, long.MinValue, 0, 3_000_000_000, -7 };
            t.Root.Assume(values.Select(v => t.Num.Of(v)).ToArray());
            Var<long> x = null!;
            var q = t.Graph.DefineQuery("nums", b =>
            {
                x = b.Var<long>("x");
                b.When(t.Num.Of(x));
            });
            var seen = new List<long>();
            t.Root.ForEachMatch(q, m =>
            {
                seen.Add(m.Get(x));
                return true;
            });
            Assert.Equal(values.OrderBy(v => v).ToArray(), seen.ToArray());
            var rows = t.Root.Rows(t.Num).Select(r => (long)r.Keys[0]).ToArray();
            Assert.Equal(values.OrderBy(v => v).ToArray(), rows);
        }

        [Fact]
        public void P07_ContentOrderOfStrings()
        {
            var g = new EGraph();
            var s = g.DefineRelation<string>("s");
            var values = new[] { "b", "a", "B", "ä", "aa", "" };
            g.Root.Assume(values.Select(v => s.Of(v)).ToArray());
            var rows = g.Root.Rows(s).Select(r => (string)r.Keys[0]).ToArray();
            Assert.Equal(values.OrderBy(v => v, StringComparer.Ordinal).ToArray(), rows);
        }

        [Fact]
        public void P08_ContentOrderOfDoubles()
        {
            var g = new EGraph();
            var s = g.DefineRelation<double>("d");
            var values = new[] { 1.5, -2.5, 0.0, 1e300, -1e-300, 3.0 };
            g.Root.Assume(values.Select(v => s.Of(v)).ToArray());
            var rows = g.Root.Rows(s).Select(r => (double)r.Keys[0]).ToArray();
            Assert.Equal(values.OrderBy(v => v).ToArray(), rows);
        }

        [Fact]
        public void P09_ObserverWritesAreRefusedAndTheFlagIsReset()
        {
            var t = new NodeTheory();
            t.Root.Assume(t.Num.Of(1L));
            t.Rules.DefineRule("next", r =>
            {
                var x = r.Var<long>("x");
                r.When(t.Num.Of(x));
                r.Where(Primitives.Less.Of(x, 3L));
                r.Then(t.Num.Of(Primitives.Add.Of(x, 1L)));
            });
            var errors = new List<Exception?>();
            var q = t.Graph.DefineQuery("q", b =>
            {
                var x = b.Var<long>("x");
                b.When(t.Num.Of(x));
            });
            var report = t.Root.Run(Schedule.Saturate(t.Rules), new RunOptions
            {
                Observer = step =>
                {
                    errors.Add(Record.Exception(() => t.Root.Make(t.A.Of())));
                    errors.Add(Record.Exception(() => t.Root.Union(t.Root.Make(t.A.Of()), t.Root.Make(t.A.Of()))));
                    errors.Add(Record.Exception(() => t.Root.Write(w => { })));
                    errors.Add(Record.Exception(() => t.Root.ForEachMatch(q, m =>
                    {
                        t.Root.Assume(t.Num.Of(100L));
                        return true;
                    })));
                    Assert.True(t.Root.Count(q) >= 1);
                },
            });
            Assert.All(errors, e => Assert.IsType<InvalidOperationException>(e));
            Assert.False(t.Root.Check(t.Num.Of(100L)));
            t.Root.Assume(t.Num.Of(100L));
            Assert.True(t.Root.Check(t.Num.Of(100L)));
            Assert.Equal(RunStopReason.Completed, report.StopReason);
        }

        [Fact]
        public void P10_AThrowingObserverDoesNotFaultAndResetsTheFlag()
        {
            var t = new NodeTheory();
            t.Root.Assume(t.Num.Of(1L));
            t.Rules.DefineRule("next", r =>
            {
                var x = r.Var<long>("x");
                r.When(t.Num.Of(x));
                r.Where(Primitives.Less.Of(x, 3L));
                r.Then(t.Num.Of(Primitives.Add.Of(x, 1L)));
            });
            Assert.Throws<FormatException>(() => t.Root.Run(Schedule.Saturate(t.Rules), new RunOptions
            {
                Observer = _ => throw new FormatException("boom"),
            }));
            Assert.False(t.Root.IsFaulted);
            t.Root.Assume(t.Num.Of(50L));
            Assert.Equal(RunStopReason.Completed, t.Root.Run(Schedule.Saturate(t.Rules)).StopReason);
        }

        [Fact]
        public void P11_StepsMatchIterationsAndRuleTotals()
        {
            var t = new NodeTheory();
            t.Root.Assume(t.Num.Of(1L));
            var rule = t.Rules.DefineRule("next", r =>
            {
                var x = r.Var<long>("x");
                r.When(t.Num.Of(x));
                r.Where(Primitives.Less.Of(x, 5L));
                r.Then(t.Num.Of(Primitives.Add.Of(x, 1L)));
            });
            var observed = new List<RunStep>();
            var report = t.Root.Run(Schedule.Saturate(t.Rules), new RunOptions { RecordSteps = true, Observer = observed.Add });
            Assert.Equal(report.Iterations, report.Steps.Count);
            Assert.Equal(report.Steps, observed);
            Assert.Equal(Enumerable.Range(0, report.Steps.Count), report.Steps.Select(s => s.Index));
            Assert.Equal(report.Rules[rule].Matches, report.Steps.SelectMany(s => s.Rules).Where(r => r.Rule == rule).Sum(r => r.Matches));
            Assert.Equal(report.Rules[rule].IterationsWithMatches, report.Steps.Count(s => s.Rules.Any(r => r.Rule == rule)));
            Assert.False(report.Steps[^1].Changed);
            Assert.True(report.Steps[0].Changed);
        }

        [Fact]
        public void P12_PlansAreRecorded()
        {
            var t = new NodeTheory(new EGraphOptions { RecordPlans = true });
            t.Root.Assume(t.Num.Of(1L));
            var rule = t.Rules.DefineRule("next", r =>
            {
                var x = r.Var<long>("x");
                r.When(t.Num.Of(x));
                r.Where(Primitives.Less.Of(x, 5L));
                r.Then(t.Num.Of(Primitives.Add.Of(x, 1L)));
            });
            var report = t.Root.Run(Schedule.Saturate(t.Rules), new RunOptions { RecordSteps = true });
            Assert.False(string.IsNullOrEmpty(report.Rules[rule].Plan));
            Assert.All(report.Steps.SelectMany(s => s.Rules), r => Assert.False(string.IsNullOrEmpty(r.Plan)));

            var plain = new NodeTheory();
            plain.Root.Assume(plain.Num.Of(1L));
            var r2 = plain.Rules.DefineRule("next", r =>
            {
                var x = r.Var<long>("x");
                r.When(plain.Num.Of(x));
                r.Where(Primitives.Less.Of(x, 5L));
                r.Then(plain.Num.Of(Primitives.Add.Of(x, 1L)));
            });
            Assert.Null(plain.Root.Run(Schedule.Saturate(plain.Rules)).Rules[r2].Plan);
        }

        [Fact]
        public void P13_UntilStopsWhenTheConditionHolds()
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
        public void P14_CountRowsAndRowsAgree()
        {
            var t = new NodeTheory();
            var a = t.Root.Make(t.Lit.Of(1L));
            var b = t.Root.Make(t.Lit.Of(2L));
            t.Root.Make(t.Wrap.Of(a));
            t.Root.Make(t.Wrap.Of(b));
            t.Root.Union(a, b);
            Assert.Equal(t.Root.Rows(t.Wrap).Count, t.Root.CountRows(t.Wrap));
            Assert.Equal(1, t.Root.CountRows(t.Wrap));
            Assert.Equal(2, t.Root.CountRows(t.Lit));
            Assert.Equal(0, t.Root.CountRows(t.Graph.Contradiction));
            t.Root.Assume(t.Graph.Contradiction.Of());
            Assert.Equal(1, t.Root.CountRows(t.Graph.Contradiction));
            Assert.Equal("(contradiction)", t.Root.Rows(t.Graph.Contradiction)[0].ToString());
        }

        [Fact]
        public void P15_QueryVariableBoundToAConstantAndUnifiedVariables()
        {
            var g = new EGraph();
            var e = g.DefineRelation<long, long>("e");
            g.Root.Assume(e.Of(1L, 1L), e.Of(1L, 2L), e.Of(2L, 2L), e.Of(3L, 1L));
            Var<long> x = null!;
            Var<long> y = null!;
            var loops = g.DefineQuery("loops", b =>
            {
                (x, y) = b.Vars<long, long>("x", "y");
                b.When(e.Of(x, y), x.Is(y));
            });
            var pairs = new List<(long, long)>();
            g.Root.ForEachMatch(loops, m =>
            {
                pairs.Add((m.Get(x), m.Get(y)));
                return true;
            });
            Assert.Equal(new[] { (1L, 1L), (2L, 2L) }, pairs);

            Var<long> c = null!;
            Var<long> d = null!;
            var fixedOne = g.DefineQuery("fixed", b =>
            {
                (c, d) = b.Vars<long, long>("c", "d");
                b.When(c.Is(1L), e.Of(c, d));
            });
            var got = new List<(long, long)>();
            g.Root.ForEachMatch(fixedOne, m =>
            {
                got.Add((m.Get(c), m.Get(d)));
                return true;
            });
            Assert.Equal(new[] { (1L, 1L), (1L, 2L) }, got);
        }

        [Fact]
        public void P16_QueryMatchesWhatARuleWouldMatch()
        {
            var g = new EGraph();
            var e = g.DefineRelation<long, long>("e");
            var path = g.DefineRelation<long, long>("path");
            var rs = g.DefineRuleset("rs");
            rs.DefineRule("collect", r =>
            {
                var (x, y, z) = r.Vars<long, long, long>("x", "y", "z");
                r.When(e.Of(x, y), e.Of(y, z));
                r.Where(Primitives.Less.Of(x, z));
                r.Then(path.Of(x, z));
            });
            Var<long> qx = null!;
            Var<long> qz = null!;
            var q = g.DefineQuery("two", b =>
            {
                var y = b.Var<long>("y");
                (qx, qz) = b.Vars<long, long>("x", "z");
                b.When(e.Of(qx, y), e.Of(y, qz));
                b.Where(Primitives.Less.Of(qx, qz));
            });
            var rnd = new Random(3);
            for (var i = 0; i < 40; i++)
                g.Root.Assume(e.Of((long)rnd.Next(10), (long)rnd.Next(10)));

            g.Root.Run(rs);
            var fromRule = g.Root.Rows(path).Select(r => ((long)r.Keys[0], (long)r.Keys[1])).ToHashSet();
            var fromQuery = new HashSet<(long, long)>();
            g.Root.ForEachMatch(q, m =>
            {
                fromQuery.Add((m.Get(qx), m.Get(qz)));
                return true;
            });
            Assert.Equal(fromRule.OrderBy(p => p), fromQuery.OrderBy(p => p));
        }

        [Fact]
        public void P18_ReadsAndQueriesWithoutPrimitivesDoNotIntern()
        {
            var g = new EGraph();
            var e = g.DefineRelation<string, long>("e");
            g.Root.Assume(e.Of("a", 1L), e.Of("b", 5_000_000_000L));
            Var<string> s = null!;
            Var<long> n = null!;
            var q = g.DefineQuery("q", b =>
            {
                (s, n) = b.Vars<string, long>("s", "n");
                b.When(e.Of(s, n));
            });
            var before = g.Values.InternedCount;
            Assert.Equal(2, g.Root.Count(q));
            Assert.True(g.Root.Any(q));
            g.Root.ForEachMatch(q, m =>
            {
                m.Get(s);
                m.Get(n);
                return true;
            });
            g.Root.Rows(e);
            g.Root.CountRows(e);
            Assert.Equal(before, g.Values.InternedCount);
        }

        [Fact]
        public void P19_ALetCheckAgainstAConstantInAQuery()
        {
            var t = new NodeTheory();
            t.Root.Assume(t.Num.Of(1L), t.Num.Of(2L), t.Num.Of(3L));
            Var<long> x = null!;
            var q = t.Graph.DefineQuery("q", b =>
            {
                x = b.Var<long>("x");
                b.When(t.Num.Of(x), Primitives.Add.Of(x, 1L).Is(3L));
            });
            var seen = new List<long>();
            t.Root.ForEachMatch(q, m =>
            {
                seen.Add(m.Get(x));
                return true;
            });
            Assert.Equal(new[] { 2L }, seen);
        }

        [Fact]
        public void P20_LimitPrecedence()
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
        public void P21_AStepWithMatchesThatChangedNothing()
        {
            var t = new NodeTheory();
            t.Root.Assume(t.Num.Of(1L));
            var rule = t.Rules.DefineRule("same", r =>
            {
                var x = r.Var<long>("x");
                r.When(t.Num.Of(x));
                r.Then(t.Num.Of(x));
            });
            var report = t.Root.Run(Schedule.Repeat(2, t.Rules), new RunOptions { RecordSteps = true });
            Assert.Single(report.Steps);
            Assert.False(report.Steps[0].Changed);
            Assert.Single(report.Steps[0].Rules);
            Assert.Equal(1, report.Steps[0].Rules[0].Matches);
            Assert.True(report.Saturated);
        }

        [Fact]
        public void P22_AQueryFailingInsideAnObserverDoesNotFault()
        {
            var t = new NodeTheory();
            t.Root.Assume(t.Num.Of(1L));
            t.Rules.DefineRule("next", r =>
            {
                var x = r.Var<long>("x");
                r.When(t.Num.Of(x));
                r.Where(Primitives.Less.Of(x, 3L));
                r.Then(t.Num.Of(Primitives.Add.Of(x, 1L)));
            });
            var boom = Primitive.Create<long, bool>("boom", _ => throw new InvalidOperationException("x"));
            var failing = t.Graph.DefineQuery("failing", b =>
            {
                var x = b.Var<long>("x");
                b.When(t.Num.Of(x));
                b.Where(boom.Of(x));
            });
            var caught = 0;
            var report = t.Root.Run(Schedule.Saturate(t.Rules), new RunOptions
            {
                Observer = _ =>
                {
                    try
                    {
                        t.Root.Count(failing);
                    }
                    catch (RuleEvaluationException)
                    {
                        caught++;
                    }
                },
            });
            Assert.Equal(report.Iterations, caught);
            Assert.False(t.Root.IsFaulted);
            Assert.Equal(RunStopReason.Completed, report.StopReason);
            Assert.True(t.Root.Check(t.Num.Of(3L)));
        }

        [Fact]
        public void P24_AnotherGraphsHandleInAnUntilCondition()
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

        [Fact]
        public void P25_AnotherGraphsHandleInAQueryOrRows()
        {
            var t = new NodeTheory();
            var other = new NodeTheory();
            Assert.ThrowsAny<Exception>(() => t.Graph.DefineQuery("q", b =>
            {
                var x = b.Var<long>("x");
                b.When(other.Num.Of(x));
            }));
            Assert.Throws<ArgumentException>(() => t.Root.Rows(other.Num));
            Assert.Throws<ArgumentException>(() => t.Root.CountRows(other.Num));
        }

        [Fact]
        public void P27_QueriesOnAParallelGraph()
        {
            var t = new NodeTheory(new EGraphOptions { DegreeOfParallelism = 4 });
            var e = t.Graph.DefineRelation<long, long>("e");
            var facts = new Fact[30000];
            for (var i = 0; i < facts.Length; i++)
                facts[i] = e.Of((long)i, (long)(i % 97));
            t.Root.Assume(facts);
            Var<long> a = null!;
            Var<long> b = null!;
            var q = t.Graph.DefineQuery("q", qb =>
            {
                (a, b) = qb.Vars<long, long>("a", "b");
                qb.When(e.Of(a, b), e.Of(b, 5L));
            });
            // e(b, 5) holds only for b = 5, so a match is any row whose second column is 5.
            var expected = Enumerable.Range(0, 30000).Count(i => i % 97 == 5);
            var count = t.Root.Count(q);
            var seen = new List<(long, long)>();
            t.Root.ForEachMatch(q, m =>
            {
                seen.Add((m.Get(a), m.Get(b)));
                return true;
            });
            Assert.Equal(seen.Count, count);
            Assert.Equal(seen.OrderBy(x => x).ToList(), seen);
            Assert.True(t.Root.Any(q));
            Assert.Equal(expected, count);
        }

    }

}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

using Eggnog.Tests.Support;

using Xunit;

namespace Eggnog.Tests.Rules
{

    /// <summary>
    /// What host queries match and return: matches in content order, variables bound to constants or unified, lets
    /// checked against constants, empty bodies and constant filters, handles of another graph, queries inside run
    /// observers and callbacks, and queries on a parallel graph; a query leaves an extractor usable and interns
    /// nothing. Unlike <see cref="QueryTests"/>, whose allocation probes run on their own, these tests run in parallel
    /// with the other test classes.
    /// </summary>
    public sealed class QueryMatchTests
    {

        /// <summary>
        /// Content order is numeric, negatives and the extremes included, for matches and rows alike.
        /// </summary>
        [Fact]
        public void ContentOrderIsNumericForNegativeNumbers()
        {
            var t = new NodeTheory();
            Var<long> x = null!;
            var query = t.Graph.DefineQuery("nums", q =>
            {
                x = q.Var<long>("x");
                q.When(t.Num.Of(x));
            });
            t.Root.Assume(t.Num.Of(3L), t.Num.Of(-5L), t.Num.Of(long.MinValue), t.Num.Of(-1L), t.Num.Of(long.MaxValue));
            var seen = new List<long>();
            t.Root.ForEachMatch(query, m =>
            {
                seen.Add(m.Get(x));
                return true;
            });

            Assert.Equal(new[] { long.MinValue, -5L, -1L, 3L, long.MaxValue }, seen);
            Assert.Equal(seen, t.Root.Rows(t.Num).Select(r => (long)r.Keys[0]));
        }

        /// <summary>
        /// The first call of a new query plans it, and still leaves an extractor usable.
        /// </summary>
        [Fact]
        public void AFirstCallOfANewQueryLeavesAnExtractorUsable()
        {
            var t = new NodeTheory();
            var a = t.Root.Make(t.Wrap.Of(t.A.Of()));
            var extractor = t.Root.CreateExtractor();
            var fresh = t.Graph.DefineQuery("wraps", q =>
            {
                var (n, m) = q.Vars<Node, Node>("n", "m");
                q.When(n.Is(t.Wrap.Of(m)));
            });

            Assert.Equal(1, t.Root.Count(fresh));
            Assert.Equal("(Wrap (A))", extractor.Extract(a).ToString());
        }

        /// <summary>
        /// A query constant made by a <c>Write</c> batch that did not commit is refused at each call.
        /// </summary>
        [Fact]
        public void AnUncommittedBatchsClassIsRefusedByAQuery()
        {
            var t = new NodeTheory();
            Node made = default;
            t.Root.Write(w =>
            {
                made = w.Make(t.A.Of());
                BatchRejection.FromAnotherThread(w, t.Num.Of(1L));
            });
            var query = t.Graph.DefineQuery("wrapped", q =>
            {
                var n = q.Var<Node>("n");
                q.When(n.Is(t.Wrap.Of(made)));
            });

            var error = Assert.Throws<ArgumentException>(() => t.Root.Count(query));
            Assert.Contains("did not commit", error.Message, StringComparison.Ordinal);
        }

        /// <summary>
        /// A query that fails inside a run observer ends the run with its <see cref="RuleEvaluationException"/> and
        /// faults nothing; a query disposed inside an observer is freed at once.
        /// </summary>
        [Fact]
        public void QueriesInsideAnObserverFailWithoutFaultingAndDisposeAtOnce()
        {
            var t = new NodeTheory();
            t.Rules.DefineRule("grow", r =>
            {
                var x = r.Var<long>("x");
                r.When(t.Num.Of(x));
                r.Where(Primitives.Less.Of(x, 3L));
                r.Then(t.Num.Of(Primitives.Add.Of(x, 1L)));
            });
            t.Root.Assume(t.Num.Of(0L));
            var boom = t.Graph.DefineQuery("boom", q =>
            {
                var x = q.Var<long>("x");
                q.When(t.Num.Of(x));
                q.Where(x, v => v == 1 ? throw new InvalidOperationException("one") : true);
            });
            Assert.Throws<RuleEvaluationException>(() => t.Root.Run(Schedule.Saturate(t.Rules), new RunOptions
            {
                Observer = step => step.Scope.Count(boom),
            }));
            Assert.False(t.Root.IsFaulted);

            var plain = t.Graph.DefineQuery("plain", q => q.When(t.Num.Of(q.Var<long>("x"))));
            Assert.Equal(2, t.Root.Count(plain));
            var description = plain.Compiled.Variants.Default!.Description;
            Assert.Equal(RunStopReason.Completed, t.Root.Run(Schedule.Saturate(t.Rules), new RunOptions { Observer = _ => plain.Dispose() }).StopReason);
            Assert.False(t.Graph.VariantsByDescription.ContainsKey(description));
            Assert.Throws<ObjectDisposedException>(() => t.Root.Count(plain));
        }

        /// <summary>
        /// A ForEachMatch callback that throws releases the query and the guard: Dispose frees at once, and another
        /// thread may read the scope.
        /// </summary>
        [Fact]
        public void ACallbackThatThrowsReleasesTheQueryAndTheGuard()
        {
            var t = new NodeTheory();
            var query = t.Graph.DefineQuery("q", q => q.When(t.Num.Of(q.Var<long>("x"))));
            t.Root.Assume(t.Num.Of(1L), t.Num.Of(2L));
            Assert.Throws<InvalidOperationException>(() => t.Root.ForEachMatch(query, _ => throw new InvalidOperationException("callback")));
            Assert.Equal(0, query.Compiled.Users);

            var description = query.Compiled.Variants.Default!.Description;
            query.Dispose();
            Assert.False(t.Graph.VariantsByDescription.ContainsKey(description));
            long other = -1;
            var thread = new Thread(() => other = t.Root.CountRows(t.Num));
            thread.Start();
            thread.Join();
            Assert.Equal(2, other);
        }

        [Fact]
        public void DefiningAQueryKeepsALiveExtractorUsable()
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
        public void AQueryWithAnEmptyBodyHasOneMatch()
        {
            var t = new NodeTheory();
            var empty = t.Graph.DefineQuery("empty", b => { });
            Assert.True(t.Root.Check());
            Assert.Equal(1, t.Root.Count(empty));
            Assert.True(t.Root.Any(empty));
        }

        [Fact]
        public void AQueryWithOnlyAConstantFilter()
        {
            var t = new NodeTheory();
            var yes = t.Graph.DefineQuery("yes", b => b.Where(Primitives.Greater.Of(2L, 1L)));
            var no = t.Graph.DefineQuery("no", b => b.Where(Primitives.Greater.Of(1L, 2L)));
            Assert.Equal(1, t.Root.Count(yes));
            Assert.Equal(0, t.Root.Count(no));
        }

        [Fact]
        public void ContentOrderOfNegativeAndLargeNumbers()
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
        public void QueryVariableBoundToAConstantAndUnifiedVariables()
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
        public void QueryMatchesWhatARuleWouldMatch()
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
        public void ReadsAndQueriesWithoutPrimitivesDoNotIntern()
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
        public void ALetCheckAgainstAConstantInAQuery()
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
        public void AQueryFailingInsideAnObserverDoesNotFault()
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
        public void AnotherGraphsHandleInAQueryOrRows()
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
        public void QueriesOnAParallelGraph()
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

        /// <summary>
        /// <c>QueryMatch.Get</c> returns a class canonical in the queried scope, and <c>ForEachMatch</c> lets its
        /// callback call the scope: a union made in the callback leaves the later matches' classes canonical.
        /// </summary>
        [Fact]
        public void AMatchDeliveredAfterACallbackUnionIsCanonical()
        {
            var t = new NodeTheory();
            var one = t.Root.Make(t.Lit.Of(1L));
            var two = t.Root.Make(t.Lit.Of(2L));
            Var<Node> n = null!;
            var lits = t.Graph.DefineQuery("lits", b =>
            {
                n = b.Var<Node>("n");
                var x = b.Var<long>("x");
                b.When(n.Is(t.Lit.Of(x)));
            });

            var delivered = new List<(Node Class, Node Canonical)>();
            t.Root.ForEachMatch(lits, m =>
            {
                var current = m.Get(n);
                delivered.Add((current, t.Root.Find(current)));
                if (delivered.Count == 1)
                    t.Root.Union(one, two);

                return true;
            });

            Assert.Equal(2, delivered.Count);
            Assert.All(delivered.Skip(1), d => Assert.Equal(d.Canonical, d.Class));
        }

        /// <summary>
        /// <c>ForEachMatch</c> delivers matches in content order: strings ordinal, numbers numeric. Small non-negative
        /// numbers have cells already in numeric order, so the rows include negative and large numbers, whose cells are
        /// not.
        /// </summary>
        [Fact]
        public void ForEachMatchDeliversStringsAndNumbersInContentOrder()
        {
            var g = new EGraph();
            var e = g.DefineRelation<string, long>("e");
            var rows = new (string, long)[] { ("b", 5), ("a", -1), ("B", long.MaxValue), ("a", 3_000_000_000), ("", -7), ("a", 0) };
            g.Root.Assume(rows.Select(r => e.Of(r.Item1, r.Item2)).ToArray());
            Var<string> s = null!;
            Var<long> v = null!;
            var q = g.DefineQuery("q", b =>
            {
                (s, v) = b.Vars<string, long>("s", "v");
                b.When(e.Of(s, v));
            });

            var seen = new List<(string, long)>();
            g.Root.ForEachMatch(q, m =>
            {
                seen.Add((m.Get(s), m.Get(v)));
                return true;
            });

            var expected = rows.OrderBy(r => r.Item1, StringComparer.Ordinal).ThenBy(r => r.Item2).ToList();
            Assert.Equal(expected, seen);
        }

        /// <summary>
        /// A body let checked against a constant (<c>p(args).Is(c)</c>) is a check, in a query and in a rule alike: it
        /// filters the matches rather than binding a fresh value. The differential programs never generate one.
        /// </summary>
        [Fact]
        public void ALetCheckedAgainstAConstantFilters()
        {
            var t = new NodeTheory();
            t.Root.Assume(t.Num.Of(1L), t.Num.Of(2L), t.Num.Of(3L));
            Var<long> x = null!;
            var q = t.Graph.DefineQuery("q", b =>
            {
                x = b.Var<long>("x");
                b.When(t.Num.Of(x), Primitives.Add.Of(x, 1L).Is(3L));
            });
            t.Rules.DefineRule("tag two", r =>
            {
                var y = r.Var<long>("y");
                r.When(t.Num.Of(y), Primitives.Add.Of(y, 1L).Is(3L));
                r.Then(t.Tag.Of(t.Lit.Of(y)));
            });

            var seen = new List<long>();
            t.Root.ForEachMatch(q, m =>
            {
                seen.Add(m.Get(x));
                return true;
            });
            t.Root.Run(t.Rules);

            Assert.Equal(new[] { 2L }, seen);
            Assert.Equal(1, t.Root.CountRows(t.Tag));
            Assert.True(t.Root.Check(t.Tag.Of(t.Lit.Of(2L))));
        }

    }

}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

using Eggnog.Tests.Cosmos;
using Eggnog.Tests.Cosmos.Scoped;
using Eggnog.Tests.Support;

using Xunit;

namespace Eggnog.Tests.Concurrency
{

    /// <summary>
    /// Children of a frozen scope run concurrently: queries under one frozen container on parallel threads give the
    /// results of serial runs, queries that split included, and host reads, queries and extractors on a frozen scope
    /// run from many threads. Most tests run on dedicated threads that meet at a barrier first
    /// (<see cref="DedicatedThreads"/>), so the calls overlap.
    /// </summary>
    public sealed class ConcurrencyTests
    {

        /// <summary>
        /// The number of threads the tests run on.
        /// </summary>
        const int Threads = 8;

        /// <summary>
        /// Asks every sweep query once, serially, and renders each extraction.
        /// </summary>
        static string[] Serial()
        {
            var c = new CosmosTheory(new EGraph());
            c.Graph.Root.Freeze();
            var schema = SweepLinks.Compile(c);
            return SweepLinks.Queries(c).Select(term =>
            {
                var (query, q) = SweepLinks.Ask(c, schema, term);
                using (query)
                    return Describe(c, query, q);
            }).ToArray();
        }

        /// <summary>
        /// Renders what a query scope proved: the extraction, its cost, the stored forms it holds, and whether it is
        /// contradictory.
        /// </summary>
        static string Describe(CosmosTheory c, Scope query, Pred q)
        {
            var best = query.Extract(q);
            var forms = string.Join(",", query.Rows(c.Form).Select(r => r.ToString()));
            return $"{best} cost {best.Cost} contradictory {query.IsContradictory} forms {forms}";
        }

        [Fact]
        public void ThirtyTwoQueriesUnderOneFrozenContainerOnParallelThreadsGiveTheSerialResults()
        {
            var expected = Serial();
            var c = new CosmosTheory(new EGraph());
            c.Graph.Root.Freeze();
            var schema = SweepLinks.Compile(c);
            var terms = SweepLinks.Queries(c);
            var results = new string[32];

            var threads = DedicatedThreads.For(Threads, results.Length, i =>
            {
                var (query, q) = SweepLinks.Ask(c, schema, terms[i % terms.Length]);
                using (query)
                    results[i] = Describe(c, query, q);
            });

            Assert.Equal(Threads, threads);

            for (var i = 0; i < results.Length; i++)
                Assert.Equal(expected[i % expected.Length], results[i]);

            Assert.Empty(schema.Children);
            Assert.Equal(0, schema.Engine.Context.Context.ChildCount);
        }

        [Fact]
        public void QueriesAndExtractorsOnAFrozenSchemaRunFromEightThreads()
        {
            var c = new CosmosTheory(new EGraph());
            var present = c.Graph.DefineQuery("present", q => q.When(c.Present.Of(q.Var<string>("p"))));
            var forms = c.Graph.DefineQuery("forms", q =>
            {
                var (p, f) = q.Vars<string, string>("p", "f");
                q.When(c.Form.Of(p, f));
            });
            c.Graph.Root.Freeze();
            var schema = c.Graph.Root.CreateChild(c.Schema);
            SweepLinks.Declare(c, schema);
            var terms = new List<Pred>();
            foreach (var term in SweepLinks.Queries(c))
                terms.Add(schema.Make(term));

            schema.Run(Schedule.Saturate(c.Horn, c.Lattice, c.Rewrite));
            schema.Freeze();
            var version = schema.Version;
            var expectedPresent = schema.Count(present);
            var expectedForms = new List<string>();
            schema.ForEachMatch(forms, m =>
            {
                expectedForms.Add(m.Get((Var<string>)forms.Variables[0]) + " " + m.Get((Var<string>)forms.Variables[1]));
                return true;
            });
            var expectedTerms = terms.Select(t => schema.Extract(t).ToString()).ToArray();

            var failures = new List<string>();
            var threads = DedicatedThreads.For(Threads, Threads * 4, i =>
            {
                var extractor = schema.CreateExtractor();
                for (var round = 0; round < 5; round++)
                {
                    var seen = new List<string>();
                    schema.ForEachMatch(forms, m =>
                    {
                        seen.Add(m.Get((Var<string>)forms.Variables[0]) + " " + m.Get((Var<string>)forms.Variables[1]));
                        return true;
                    });
                    var ok = schema.Count(present) == expectedPresent && schema.Any(present) && seen.SequenceEqual(expectedForms)
                        && terms.Select(t => extractor.Extract(t).ToString()).SequenceEqual(expectedTerms);
                    if (ok == false)
                    {
                        lock (failures)
                            failures.Add($"thread {i} round {round}");
                    }
                }
            });

            Assert.Equal(Threads, threads);
            Assert.Empty(failures);
            Assert.Equal(version, schema.Version);
            Assert.Equal(0, schema.Engine.Context.Context.ChildCount);
        }

        /// <summary>
        /// Extractors over a frozen scope are thread-safe, and the library's other objects either are thread-safe or
        /// fail fast on a second thread: one extractor shared by 8 threads over a frozen scope either extracts the
        /// right term or fails fast as in use on another thread, and never returns a wrong answer
        /// (<c>no finite term</c> included) or throws an unrelated exception.
        /// </summary>
        [Fact]
        public void OneExtractorSharedByEightThreadsOverAFrozenScope()
        {
            var t = new NodeTheory();
            var terms = new Node[400];
            for (var i = 0; i < terms.Length; i++)
                terms[i] = t.Root.Make(t.Wrap.Of(t.Add.Of(t.Lit.Of(i), t.Wrap.Of(t.Lit.Of(i + 1L)))));

            t.Root.Freeze();
            var extractor = t.Root.CreateExtractor();
            var rendered = new string?[8][];
            var errors = new System.Collections.Concurrent.ConcurrentBag<Exception>();
            using var start = new System.Threading.ManualResetEventSlim();
            RunOnThreads(rendered.Length, k =>
            {
                rendered[k] = new string?[terms.Length];
                start.Wait(TimeSpan.FromSeconds(30));
                for (var j = 0; j < terms.Length; j++)
                {
                    var i = (j * 7 + k * 53) % terms.Length;
                    try
                    {
                        rendered[k][i] = extractor.Extract(terms[i]).ToString();
                    }
                    catch (Exception exception)
                    {
                        errors.Add(exception);
                    }
                }
            }, start.Set);

            var wrong = 0;
            for (var i = 0; i < terms.Length; i++)
                foreach (var r in rendered)
                    if (r![i] is { } text && text != $"(Wrap (Add (Lit {i}) (Wrap (Lit {i + 1}))))")
                        wrong++;

            var unrelated = 0;
            foreach (var error in errors)
                if (error.GetType() != typeof(InvalidOperationException) || error.Message.Contains("another thread", StringComparison.Ordinal) == false)
                    unrelated++;

            Assert.True(wrong == 0 && unrelated == 0,
                $"{wrong} wrong terms; {errors.Count} failed calls; {unrelated} unrelated exceptions: {string.Join("; ", System.Linq.Enumerable.Distinct(System.Linq.Enumerable.Select(errors, e => e.GetType().Name + ": " + e.Message)))}");
        }

        /// <summary>
        /// Reads of a frozen scope take no guard and are thread-safe: <c>Check</c>, <c>Get</c>, <c>Find</c>,
        /// <c>AreEqual</c>, <c>CountRows</c>, <c>Rows</c> and <c>CreateChild</c> from 8 threads give the serial
        /// answers.
        /// </summary>
        [Fact]
        public void ReadsOfAFrozenScopeFromEightThreads()
        {
            var t = new NodeTheory();
            var terms = new Node[200];
            for (var i = 0; i < terms.Length; i++)
            {
                terms[i] = t.Root.Make(t.Wrap.Of(t.Lit.Of(i)));
                t.Root.Assume(t.Num.Of(i), t.Tag.Of(terms[i]));
            }

            t.Root.Freeze();
            var failures = new System.Collections.Concurrent.ConcurrentBag<string>();
            RunOnThreads(8 * 20, k =>
            {
                var i = k % terms.Length;
                if (t.Root.Check(t.Num.Of(i), t.Tag.Of(terms[i])) == false)
                    failures.Add($"check {i}");
                if (t.Root.Get(t.Wrap.Of(t.Lit.Of(i))) != t.Root.Find(terms[i]))
                    failures.Add($"get {i}");
                if (t.Root.AreEqual(terms[i], t.Wrap.Of(t.Lit.Of(i))) == false)
                    failures.Add($"equal {i}");
                if (t.Root.CountRows(t.Num) != terms.Length || t.Root.Rows(t.Tag).Count != terms.Length)
                    failures.Add($"rows {k}");
                using var child = t.Root.CreateChild();
                child.Assume(t.Num.Of(-1L - k));
                if (child.CountRows(t.Num) != terms.Length + 1)
                    failures.Add($"child {k}");
            }, null);

            Assert.Empty(failures);
            Assert.Empty(t.Root.Children);
        }

        /// <summary>
        /// Runs <paramref name="body"/> for 0..<paramref name="count"/>-1 on 8 dedicated threads (never pool threads,
        /// so the suite's pool-sensitive tests running alongside are not starved), calling <paramref name="started"/>
        /// once every thread is running, and waits for them.
        /// </summary>
        /// <param name="count">The number of calls.</param>
        /// <param name="body">The call, given its index.</param>
        /// <param name="started">Called once every thread is running; <c>null</c> for nothing.</param>
        static void RunOnThreads(int count, Action<int> body, Action? started)
        {
            var next = -1;
            var threads = new System.Threading.Thread[Math.Min(8, count)];
            for (var n = 0; n < threads.Length; n++)
            {
                threads[n] = new System.Threading.Thread(() =>
                {
                    for (var k = System.Threading.Interlocked.Increment(ref next); k < count; k = System.Threading.Interlocked.Increment(ref next))
                        body(k);
                });
                threads[n].Start();
            }

            started?.Invoke();
            foreach (var thread in threads)
                thread.Join();
        }

        /// <summary>
        /// Children of a frozen scope have their own thread domains and may run concurrently, and a split's cases run
        /// on their parent's thread. Queries that split (sharing the split rules, their sources, the graph's lift spec
        /// and the functions' case joins) run on 8 threads under one frozen schema and give the serial answer.
        /// </summary>
        [Fact]
        public void SplittingQueriesUnderOneFrozenSchemaRunOnParallelThreads()
        {
            var c = new Cosmos.Scoped.CosmosTheory(new EGraph());
            c.Graph.Root.Freeze();
            var schema = Cosmos.Scoped.SweepLinks.Compile(c);
            ConstructorTerm<Pred> Term() =>
                c.And.Of(c.Or.Of(c.StrEq.Of("data.type", "park"), c.StrEq.Of("data.type", "map")), c.UuidEq.Of("data.data.parkId", Cosmos.Scoped.SweepLinks.Park));

            string Ask()
            {
                using var query = schema.CreateChild(c.Query);
                var q = query.Make(Term());
                query.Assume(c.WhereClause.Of(q));
                var report = query.Run(c.Saturation);
                Assert.Equal(RunStopReason.Completed, report.StopReason);
                Assert.Equal(2, report.CasesOpened);
                Assert.Empty(query.Children);
                return query.Extract(q).ToString();
            }

            var serial = Ask();
            var answers = new string[32];
            Support.DedicatedThreads.For(8, answers.Length, i => answers[i] = Ask());

            Assert.All(answers, answer => Assert.Equal(serial, answer));
            Assert.Empty(schema.Children);
        }

        /// <summary>
        /// Queries under one frozen container run their split steps concurrently on parallel threads (each query scope
        /// is its own thread domain; split rules, their sources, lift specs and case joins are shared), and give the
        /// results of serial runs.
        /// </summary>
        [Fact]
        public void SplitQueriesUnderOneFrozenContainerOnParallelThreadsGiveTheSerialResults()
        {
            static string Ask(CosmosTheory c, Scope schema, ConstructorTerm<Pred> term)
            {
                using var query = schema.CreateChild(c.Query);
                var q = query.Make(term);
                query.Assume(c.WhereClause.Of(q));
                var report = query.Run(c.Saturation);
                Assert.Empty(query.Children);
                var best = query.Extract(q);
                var forms = string.Join(",", query.Rows(c.Form).Select(r => r.ToString()));
                return $"{best} cost {best.Cost} cases {report.CasesOpened} contradictory {query.IsContradictory} forms {forms}";
            }

            var serial = new CosmosTheory(new EGraph());
            serial.Graph.Root.Freeze();
            var serialSchema = SweepLinks.Compile(serial);
            var expected = SweepLinks.Queries(serial).Select(term => Ask(serial, serialSchema, term)).ToArray();
            Assert.Contains(expected, e => e.Contains("cases 2", StringComparison.Ordinal));

            var c = new CosmosTheory(new EGraph());
            c.Graph.Root.Freeze();
            var schema = SweepLinks.Compile(c);
            var terms = SweepLinks.Queries(c);
            var results = new string[32];

            var threads = DedicatedThreads.For(8, results.Length, i => results[i] = Ask(c, schema, terms[i % terms.Length]));

            Assert.Equal(8, threads);
            for (var i = 0; i < results.Length; i++)
                Assert.Equal(expected[i % expected.Length], results[i]);

            Assert.Empty(schema.Children);
            Assert.Equal(0, schema.Engine.Context.Context.ChildCount);
        }

        /// <summary>
        /// The reference the queries compare a UUID path with.
        /// </summary>
        const string Ref = "123e4567-e89b-12d3-a456-426614174000";

        /// <summary>
        /// Children of a frozen scope may run concurrently, and each query's splits are its own: eight queries under
        /// one frozen container run the scoped saturation, whose query split opens their cases, on pool threads and
        /// reach the serial result; the schema learns nothing from them.
        /// </summary>
        [Fact]
        public void SplitsInSiblingQueriesRunConcurrentlyAndAgreeWithASerialRun()
        {
            var graph = new EGraph();
            var c = new CosmosTheory(graph);
            graph.Root.Freeze();
            var schema = graph.Root.CreateChild(c.Schema);
            schema.Assume(
                c.Present.Of("kind"),
                c.Dom.Of("kind").Is(ValueSet.Of("order", "invoice", "note")),
                c.Discriminator.Of("kind"));
            c.DeclareBranch(schema, "kind", "order", c.Form.Of("ref", "uuid-lower"));
            c.DeclareBranch(schema, "kind", "invoice", c.Form.Of("ref", "uuid-lower"));
            Assert.Equal(3, schema.Run(c.Facts).CasesOpened);
            schema.Freeze();

            string RunOne()
            {
                using var query = schema.CreateChild(c.Query);
                var q = query.Make(c.And.Of(
                    c.Or.Of(c.StrEq.Of("kind", "order"), c.StrEq.Of("kind", "invoice")),
                    c.UuidEq.Of("ref", Ref)));
                query.Assume(c.WhereClause.Of(q));
                var report = query.Run(c.Saturation);
                Assert.Equal(RunStopReason.Completed, report.StopReason);
                Assert.Equal(2, report.CasesOpened);
                Assert.Empty(query.Children);
                Assert.True(query.Check(c.Form.Of("ref", "uuid-lower")));
                return query.Extract(q).ToString();
            }

            var serial = RunOne();
            for (var round = 0; round < 4; round++)
            {
                var results = new string[8];
                Parallel.For(0, results.Length, new ParallelOptions { MaxDegreeOfParallelism = 8 }, i => results[i] = RunOne());
                Assert.All(results, r => Assert.Equal(serial, r));
            }

            Assert.False(schema.Check(c.Form.Of("ref", "uuid-lower")));
        }

    }

}

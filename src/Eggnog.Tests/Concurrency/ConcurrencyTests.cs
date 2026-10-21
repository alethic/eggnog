using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

using Eggnog.Tests.Predicates;
using Eggnog.Tests.Predicates.Scoped;
using Eggnog.Tests.Support;

using Xunit;

namespace Eggnog.Tests.Concurrency
{

    /// <summary>
    /// Children of a frozen scope run concurrently: queries under one frozen schema on parallel threads give the
    /// results of serial runs, queries that split included, and host reads, queries and extractors on a frozen scope
    /// run from many threads. Most tests run on dedicated threads that meet at a barrier first
    /// (<see cref="DedicatedThreads"/>), so the calls overlap. <see cref="Soak"/> runs random queries the same way over
    /// fresh seeds, query counts and thread counts.
    /// </summary>
    public sealed class ConcurrencyTests
    {

        /// <summary>
        /// The number of threads the tests run on.
        /// </summary>
        const int Threads = 8;

        readonly ITestOutputHelper output;

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        public ConcurrencyTests(ITestOutputHelper output)
        {
            this.output = output;
        }

        /// <summary>
        /// Asks every query of the discriminated schema (<see cref="DiscriminatedSchema.Queries"/>) once, serially, and
        /// renders each extraction.
        /// </summary>
        static string[] Serial()
        {
            var c = new PredicateTheory(new EGraph());
            c.Graph.Root.Freeze();
            var schema = DiscriminatedSchema.Compile(c);
            return DiscriminatedSchema.Queries(c).Select(term =>
            {
                var (query, q) = DiscriminatedSchema.Ask(c, schema, term);
                using (query)
                    return Describe(c, query, q);
            }).ToArray();
        }

        /// <summary>
        /// Renders what a query scope proved: the extraction, its cost, the stored forms it holds, and whether it is
        /// contradictory.
        /// </summary>
        static string Describe(PredicateTheory c, Scope query, Pred q)
        {
            var best = query.Extract(q);
            var forms = string.Join(",", query.Rows(c.Form).Select(r => r.ToString()));
            return $"{best} cost {best.Cost} contradictory {query.IsContradictory} forms {forms}";
        }

        [Fact]
        public void ThirtyTwoQueriesUnderOneFrozenSchemaOnParallelThreadsGiveTheSerialResults()
        {
            var expected = Serial();
            var c = new PredicateTheory(new EGraph());
            c.Graph.Root.Freeze();
            var schema = DiscriminatedSchema.Compile(c);
            var terms = DiscriminatedSchema.Queries(c);
            var results = new string[32];

            var threads = DedicatedThreads.For(Threads, results.Length, i =>
            {
                var (query, q) = DiscriminatedSchema.Ask(c, schema, terms[i % terms.Length]);
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
            var c = new PredicateTheory(new EGraph());
            var present = c.Graph.DefineQuery("present", q => q.When(c.Present.Of(q.Var<string>("p"))));
            var forms = c.Graph.DefineQuery("forms", q =>
            {
                var (p, f) = q.Vars<string, string>("p", "f");
                q.When(c.Form.Of(p, f));
            });
            c.Graph.Root.Freeze();
            var schema = c.Graph.Root.CreateChild(c.Schema);
            DiscriminatedSchema.Declare(c, schema);
            var terms = new List<Pred>();
            foreach (var term in DiscriminatedSchema.Queries(c))
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
        /// One extractor shared by 8 threads over a frozen scope serializes its cost queries on its lock, as it does its
        /// extractions: every thread gets each class's cost, and no call fails.
        /// </summary>
        [Fact]
        public void OneExtractorCostsFromEightThreadsOverAFrozenScope()
        {
            var t = new NodeTheory();
            var terms = new Node[400];
            for (var i = 0; i < terms.Length; i++)
                terms[i] = t.Root.Make(t.Wrap.Of(t.Add.Of(t.Lit.Of(i), t.Wrap.Of(t.Lit.Of(i + 1L)))));

            t.Root.Freeze();
            var serial = t.Root.CreateExtractor();
            var expected = new long[terms.Length];
            for (var i = 0; i < terms.Length; i++)
                Assert.True(serial.TryGetCost(terms[i], out expected[i]));

            var extractor = t.Root.CreateExtractor();
            var wrong = 0;
            var errors = new System.Collections.Concurrent.ConcurrentBag<Exception>();
            using var start = new System.Threading.ManualResetEventSlim();
            RunOnThreads(8, k =>
            {
                start.Wait(TimeSpan.FromSeconds(30));
                for (var j = 0; j < terms.Length; j++)
                {
                    var i = (j * 7 + k * 53) % terms.Length;
                    try
                    {
                        if (extractor.TryGetCost(terms[i], out var cost) == false || cost != expected[i])
                            System.Threading.Interlocked.Increment(ref wrong);
                    }
                    catch (Exception exception)
                    {
                        errors.Add(exception);
                    }
                }
            }, start.Set);

            Assert.True(wrong == 0 && errors.IsEmpty,
                $"{wrong} wrong costs; {errors.Count} failed calls: {string.Join("; ", System.Linq.Enumerable.Distinct(System.Linq.Enumerable.Select(errors, e => e.GetType().Name + ": " + e.Message)))}");
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
            var c = new Predicates.Scoped.PredicateTheory(new EGraph());
            c.Graph.Root.Freeze();
            var schema = Predicates.Scoped.DiscriminatedSchema.Compile(c);
            ConstructorTerm<Pred> Term() =>
                c.And.Of(c.Or.Of(c.StrEq.Of("kind", "a"), c.StrEq.Of("kind", "b")), c.UuidEq.Of("body.ref", Predicates.Scoped.DiscriminatedSchema.Ref));

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
        /// Queries under one frozen schema run their split steps concurrently on parallel threads (each query scope
        /// is its own thread domain; split rules, their sources, lift specs and case joins are shared), and give the
        /// results of serial runs.
        /// </summary>
        [Fact]
        public void SplitQueriesUnderOneFrozenSchemaOnParallelThreadsGiveTheSerialResults()
        {
            static string Ask(PredicateTheory c, Scope schema, ConstructorTerm<Pred> term)
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

            var serial = new PredicateTheory(new EGraph());
            serial.Graph.Root.Freeze();
            var serialSchema = DiscriminatedSchema.Compile(serial);
            var expected = DiscriminatedSchema.Queries(serial).Select(term => Ask(serial, serialSchema, term)).ToArray();
            Assert.Contains(expected, e => e.Contains("cases 2", StringComparison.Ordinal));

            var c = new PredicateTheory(new EGraph());
            c.Graph.Root.Freeze();
            var schema = DiscriminatedSchema.Compile(c);
            var terms = DiscriminatedSchema.Queries(c);
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
        /// one frozen schema run the scoped saturation, whose query split opens their cases, on pool threads and
        /// reach the serial result; the schema learns nothing from them.
        /// </summary>
        [Fact]
        public void SplitsInSiblingQueriesRunConcurrentlyAndAgreeWithASerialRun()
        {
            var graph = new EGraph();
            var c = new PredicateTheory(graph);
            graph.Root.Freeze();
            var schema = graph.Root.CreateChild(c.Schema);
            schema.Assume(
                c.Present.Of("kind"),
                c.Dom.Of("kind").Is(Set.Of("order", "invoice", "note")),
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

        /// <summary>
        /// The soak (<see cref="ConcurrentSoak"/>, <c>EGGNOG_CONCURRENT_SOAK</c>): each seed draws 2 to 64 random WHERE
        /// clauses over the discriminated schema (<see cref="RandomClause"/>), each asked with or without the query split, and
        /// runs them as queries under one frozen schema on 2 to 8 dedicated threads, each query disposed as soon as it
        /// is described. Every query must give what the same query gave when asked serially before; the schema must
        /// be unchanged and left without children. In half the iterations the queries have two threads and their rule
        /// sets are forced onto the parallel executor; their extractions are then compared by cost only, since ids,
        /// and so ties between terms of equal cost, may differ between parallel runs.
        /// </summary>
        [Fact]
        public void Soak()
        {
            ConcurrentSoak.Run(output, nameof(ConcurrencyTests), seed =>
            {
                var random = new Random(seed);
                var queries = random.Next(2, 65);
                var threads = random.Next(2, 9);
                var parallel = random.Next(2) == 0;
                return ($"{queries} queries on {threads} threads, parallel {parallel}", () => SoakOnce(seed, queries, threads, parallel));
            });
        }

        /// <summary>
        /// One iteration of <see cref="Soak"/>.
        /// </summary>
        /// <param name="seed">The seed of the clauses.</param>
        /// <param name="queries">The number of queries.</param>
        /// <param name="threads">The number of threads.</param>
        /// <param name="parallel">Whether each query has two threads and runs its rule sets on the parallel
        /// executor.</param>
        static void SoakOnce(int seed, int queries, int threads, bool parallel)
        {
            var c = new PredicateTheory(new EGraph());
            c.Graph.Root.Freeze();
            var schema = DiscriminatedSchema.Compile(c);
            var version = schema.Version;
            var random = new Random(seed);
            var jobs = Enumerable.Range(0, queries).Select(_ => (Clause: RandomClause(c, random, 3), Split: random.Next(2) == 0)).ToArray();

            string Ask(int i)
            {
                var (clause, split) = jobs[i];
                using var query = schema.CreateChild(c.Query, parallel ? new ScopeOptions { DegreeOfParallelism = 2 } : null);
                if (parallel)
                    query.Engine.Context.Context.Database.ParallelRuleSetsOverride = true;

                var q = query.Make(clause);
                query.Assume(c.WhereClause.Of(q));
                var report = query.Run(split ? c.Saturation : Schedule.Saturate(c.Horn, c.Lattice, c.Rewrite));
                Assert.Empty(query.Children);
                var best = query.Extract(q);
                var term = parallel ? "" : best.ToString();
                return $"{term} cost {best.Cost} stop {report.StopReason} cases {report.CasesOpened} contradictory {query.IsContradictory}{Environment.NewLine}{string.Join(Environment.NewLine, ScopeFacts.Of(query))}";
            }

            var expected = Enumerable.Range(0, queries).Select(Ask).ToArray();
            var results = new string[queries];
            var ran = DedicatedThreads.For(threads, queries, i => results[i] = Ask(i));

            Assert.Equal(Math.Min(threads, queries), ran);
            for (var i = 0; i < queries; i++)
                Assert.True(expected[i] == results[i], $"query {i} ({jobs[i].Clause}, split {jobs[i].Split}) differs from its serial run:{Environment.NewLine}{expected[i]}{Environment.NewLine}---{Environment.NewLine}{results[i]}");

            Assert.Equal(version, schema.Version);
            Assert.Empty(schema.Children);
            Assert.Equal(0, schema.Engine.Context.Context.ChildCount);
        }

        /// <summary>
        /// A random WHERE clause over the discriminated schema (<see cref="DiscriminatedSchema"/>), at most
        /// <paramref name="depth"/> connectives deep: conjunctions, disjunctions and negations of comparisons and tests
        /// of its paths, some of which the schema declares (the discriminator <c>kind</c>, the forms that lower
        /// <c>UuidEq</c> and <c>InstantGt</c>, the bounds of <c>seq</c>) and some of which it does not.
        /// </summary>
        /// <param name="c">The theory.</param>
        /// <param name="random">The source of choices.</param>
        /// <param name="depth">The most connectives above a leaf.</param>
        /// <returns>The clause.</returns>
        static ConstructorTerm<Pred> RandomClause(PredicateTheory c, Random random, int depth)
        {
            if (depth > 0 && random.Next(3) > 0)
            {
                return random.Next(5) switch
                {
                    0 or 1 => c.And.Of(RandomClause(c, random, depth - 1), RandomClause(c, random, depth - 1)),
                    2 or 3 => c.Or.Of(RandomClause(c, random, depth - 1), RandomClause(c, random, depth - 1)),
                    _ => c.Not.Of(RandomClause(c, random, depth - 1)),
                };
            }

            string[] kinds = ["a", "b", "c", "d"];
            string[] paths = ["kind", "label", "body.ref", "extra"];
            return random.Next(9) switch
            {
                0 or 1 => c.StrEq.Of("kind", kinds[random.Next(kinds.Length)]),
                2 => c.StrEq.Of("type", random.Next(2) == 0 ? "Record" : "Other"),
                3 => c.UuidEq.Of(random.Next(2) == 0 ? "body.ref" : "body.key", random.Next(2) == 0 ? DiscriminatedSchema.Ref : DiscriminatedSchema.Key),
                4 => c.BoolTrue.Of("flag"),
                5 => c.InstantGt.Of("meta.updated", DiscriminatedSchema.Since),
                6 => random.Next(2) == 0 ? c.NumGt.Of("seq", (long)random.Next(-2, 3)) : c.NumLt.Of("seq", (long)random.Next(-2, 3)),
                7 => c.IsDefined.Of(paths[random.Next(paths.Length)]),
                _ => c.IsString.Of(paths[random.Next(paths.Length)]),
            };
        }

    }

}

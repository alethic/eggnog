using System;
using System.Collections.Generic;
using System.Linq;

using Eggnog.Tests.Cosmos;
using Eggnog.Tests.Cosmos.Scoped;
using Eggnog.Tests.Support;

using Xunit;

namespace Eggnog.Tests.Concurrency
{

    /// <summary>
    /// Children of a frozen scope run concurrently (D17, D47, §6.12): queries under one frozen container on parallel
    /// threads give the results of serial runs, and host queries and extractors on a frozen schema run from many threads.
    /// Every test runs on dedicated threads that meet at a barrier first (<see cref="DedicatedThreads"/>), so the calls
    /// overlap.
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

    }

}

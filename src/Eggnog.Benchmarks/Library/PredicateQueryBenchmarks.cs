using System;
using System.Linq;

using BenchmarkDotNet.Attributes;

namespace Eggnog.Benchmarks.Library
{

    /// <summary>
    /// The latency of a per-request query of a compiled schema, on the predicate theory's query corpus: one query asked
    /// of its frozen schema scope in a child of it, the predicate made and assumed as the where clause, a saturating
    /// run, the answer checked, and the child disposed, the way a service asks a question of a compiled schema.
    /// <see cref="Query"/> names the query, so each is timed on its own, or asks the whole corpus in one invocation.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The theory, its schemas and its queries are the benchmarks' copy of the tests' predicate fixture
    /// (<see cref="Predicates.PredicateTheory"/>, <see cref="Predicates.ScopedQueryCases"/>,
    /// <see cref="Predicates.DiscriminatedSchema"/>), which stays as it is so that a run on a later build asks what the
    /// baseline asked; <see cref="PredicateCorpus"/> builds it in a serial graph: the theory, the empty root frozen,
    /// then every schema compiled and frozen under it as sibling scopes. Only the public API is used. Each invocation
    /// makes and disposes its own query scopes, so BenchmarkDotNet chooses the invocation count, and the memory
    /// diagnoser's allocated bytes per operation are the bytes per invocation.
    /// </para>
    /// <para>
    /// The measured region holds the query and the check of its answer, and nothing else: the predicate's term is built
    /// in the setup, as a service holds a request's predicate before it asks, and the steps are the corpus's own
    /// (<see cref="Predicates.DiscriminatedSchema.Ask"/>, or <see cref="Predicates.DiscriminatedSchema.AskWithCases"/>
    /// for the queries with the case rules). Every invocation checks its answer as the corpus defines it: a case's
    /// result by the case's own check (<see cref="Predicates.ScopedQueryCase.Holds"/>), which reads the query scope and
    /// for most cases extracts, a discriminated-schema query's by its extraction, rendered and compared with the
    /// rendering and cost the setup checked. The setup builds the whole corpus, checks that each value of
    /// <see cref="PredicateQuery"/> asks the query it names, and checks every case's result and every extraction, with
    /// and without the case rules (<see cref="PredicateCorpus"/>), then runs one invocation and checks that it left no
    /// scope alive; the cleanup checks that no query scope outlived its invocation, though BenchmarkDotNet only logs a
    /// failure there.
    /// </para>
    /// <para>
    /// Each query takes 16 to 18 ids and 11 to 34 epochs of the graph's limits (<see cref="EGraph.Usage"/>), which a
    /// disposed scope does not give back. A case's graph lives for its run only, and a run of the default job asks
    /// a few million queries at most, far below the limits. The class is not sealed because BenchmarkDotNet generates
    /// a class that derives from it.
    /// </para>
    /// </remarks>
    [LatencyColumns]
    public class PredicateQueryBenchmarks : Relations.Benchmarks.IItemsPerInvocation
    {

        PredicateCorpus? corpus;
        CorpusQuery[]? asked;

        /// <summary>
        /// Gets or sets the query asked: one of the corpus, or <see cref="PredicateQuery.Corpus"/> for all of them.
        /// </summary>
        [ParamsAllValues]
        public PredicateQuery Query { get; set; }

        /// <summary>
        /// Builds the corpus and checks every answer, then asks the benchmark's queries once.
        /// </summary>
        /// <exception cref="InvalidOperationException">A schema or a query failed its check, or a query left its scope
        /// alive.</exception>
        [GlobalSetup]
        public void GlobalSetup()
        {
            corpus = new PredicateCorpus();
            asked = Query == PredicateQuery.Corpus ? corpus.Queries.ToArray() : [corpus.Queries[(int)Query]];
            Ask();
            corpus.CheckNoLiveScope();
        }

        /// <summary>
        /// Asks the query, or every query of the corpus in order, and checks each answer.
        /// </summary>
        /// <returns>The queries asked.</returns>
        /// <exception cref="InvalidOperationException">A query's run did not saturate or left a child, a case's result
        /// does not hold, or an extraction is another term or has another cost.</exception>
        [Benchmark]
        public int Ask()
        {
            var queries = asked!;
            for (var i = 0; i < queries.Length; i++)
                queries[i].Ask();

            return queries.Length;
        }

        /// <summary>
        /// Checks that every query scope was disposed: no schema has a live child.
        /// </summary>
        /// <exception cref="InvalidOperationException">A query scope is still alive.</exception>
        [GlobalCleanup]
        public void GlobalCleanup() => corpus!.CheckNoLiveScope();

        /// <inheritdoc />
        /// <remarks>
        /// The queries one invocation asks: 1, or every query of the corpus for <see cref="PredicateQuery.Corpus"/>, so
        /// that the mean time divided by it is the time per query.
        /// </remarks>
        long Relations.Benchmarks.IItemsPerInvocation.ItemsPerInvocation(string benchmarkMethod) =>
            Query == PredicateQuery.Corpus ? (long)PredicateQuery.Corpus : 1;

    }

}

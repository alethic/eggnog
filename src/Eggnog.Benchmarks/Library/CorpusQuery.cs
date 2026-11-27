using System;

using Eggnog.Benchmarks.Library.Predicates;

namespace Eggnog.Benchmarks.Library
{

    /// <summary>
    /// One query of the predicate theory's corpus, ready to be asked of its frozen schema: the theory, the schema, the
    /// predicate's term, built once, whether the query's case rules run, and what checks the answer: a case's own check
    /// (<see cref="ScopedQueryCase.Holds"/>), or the extraction of a discriminated-schema query, its rendering and its
    /// cost.
    /// </summary>
    /// <remarks>
    /// <see cref="Ask"/> is the whole query, as the corpus asks it and as a service asks a question of a compiled
    /// schema: a child of the schema, the predicate made and assumed as the where clause, a saturating run, the check
    /// or the extraction, and disposal. The term is built once, here, rather than at each ask, as a service holds the
    /// predicate of a request before it asks, so that nothing but the query is measured.
    /// </remarks>
    internal sealed class CorpusQuery
    {

        readonly PredicateTheory theory;
        readonly Scope schema;
        readonly ConstructorTerm<Pred> term;
        readonly bool withCases;
        readonly ScopedQueryCase? queryCase;
        readonly string? extraction;
        readonly ulong cost;

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="name">What the query is, for messages.</param>
        /// <param name="theory">The theory.</param>
        /// <param name="schema">The query's frozen schema.</param>
        /// <param name="term">The query's predicate.</param>
        /// <param name="withCases">Whether the query's case rules run.</param>
        /// <param name="queryCase">The case whose check the answer must pass; <c>null</c> for an extraction.</param>
        /// <param name="extraction">The extraction's rendering; <c>null</c> for a case.</param>
        /// <param name="cost">The extraction's cost.</param>
        CorpusQuery(string name, PredicateTheory theory, Scope schema, ConstructorTerm<Pred> term, bool withCases, ScopedQueryCase? queryCase,
            string? extraction, ulong cost)
        {
            Name = name;
            this.theory = theory;
            this.schema = schema;
            this.term = term;
            this.withCases = withCases;
            this.queryCase = queryCase;
            this.extraction = extraction;
            this.cost = cost;
        }

        /// <summary>
        /// Gets what the query is, for messages: the case's name, or the discriminated-schema query's predicate.
        /// </summary>
        public string Name { get; }

        /// <summary>
        /// Prepares a case of <see cref="ScopedQueryCases"/>: asked without the case rules, and checked by the case.
        /// </summary>
        /// <param name="theory">The theory.</param>
        /// <param name="schema">The case's frozen schema.</param>
        /// <param name="queryCase">The case.</param>
        /// <returns>The query.</returns>
        public static CorpusQuery ForCase(PredicateTheory theory, Scope schema, ScopedQueryCase queryCase) =>
            new(queryCase.Name, theory, schema, queryCase.Query(theory), false, queryCase, null, 0);

        /// <summary>
        /// Prepares a query of <see cref="DiscriminatedSchema.Queries"/>, checked by its extraction.
        /// </summary>
        /// <param name="theory">The theory.</param>
        /// <param name="schema">The frozen discriminated schema.</param>
        /// <param name="term">The query's predicate.</param>
        /// <param name="withCases">Whether the query's case rules run (<see cref="DiscriminatedSchema.AskWithCases"/>)
        /// or not (<see cref="DiscriminatedSchema.Ask"/>).</param>
        /// <param name="extraction">The extraction's rendering.</param>
        /// <param name="cost">The extraction's cost.</param>
        /// <returns>The query.</returns>
        public static CorpusQuery ForDiscriminated(PredicateTheory theory, Scope schema, ConstructorTerm<Pred> term, bool withCases,
            string extraction, ulong cost) =>
            new(term.ToString() + (withCases ? " with the case rules" : ""), theory, schema, term, withCases, null, extraction, cost);

        /// <summary>
        /// Asks the query and checks its answer: a child of the schema, the predicate made and assumed as the where
        /// clause, a saturating run, the case's check or the extraction, and disposal.
        /// </summary>
        /// <exception cref="InvalidOperationException">The run did not saturate or left a child, the case's result does
        /// not hold, or the extraction is another term or has another cost.</exception>
        public void Ask()
        {
            var (query, q) = withCases ? DiscriminatedSchema.AskWithCases(theory, schema, term) : DiscriminatedSchema.Ask(theory, schema, term);
            using (query)
            {
                if (queryCase is not null)
                {
                    if (queryCase.Holds(theory, schema, query, q) == false)
                        throw new InvalidOperationException($"The result of the case '{Name}' does not hold.");

                    return;
                }

                var best = query.Extract(q);
                if (best.Cost != cost || best.ToString() != extraction)
                    throw new InvalidOperationException($"The query {Name} extracted {best} at cost {best.Cost}; expected {extraction} at cost {cost}.");
            }
        }

    }

}

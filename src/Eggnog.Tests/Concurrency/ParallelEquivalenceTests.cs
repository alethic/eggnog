using System.Collections.Generic;
using System.Linq;

using Eggnog.Tests.Predicates;
using Eggnog.Tests.Predicates.Scoped;
using Eggnog.Tests.Support;

using Xunit;

namespace Eggnog.Tests.Concurrency
{

    /// <summary>
    /// Parallel runs are equivalent to serial ones: every query of the predicate theory's scoped programs
    /// (<see cref="ScopedQueryCases"/> and <see cref="DiscriminatedSchema"/>'s), with a degree of parallelism of 4 on
    /// the graph and on the query scopes, gives every scope the serial run's facts modulo renaming of classes.
    /// Extraction is compared at 1 only, since ties may break differently in parallel.
    /// </summary>
    public sealed class ParallelEquivalenceTests
    {

        /// <summary>
        /// The degree of parallelism of the parallel runs.
        /// </summary>
        const int Degree = 4;

        /// <summary>
        /// Runs the discriminated schema and its queries; returns the schema's facts and each query's.
        /// </summary>
        static List<IReadOnlyList<string>> DiscriminatedSchemaFacts(int degree)
        {
            var c = new PredicateTheory(new EGraph(new EGraphOptions { DegreeOfParallelism = degree }));
            ForceParallel(c.Graph.Root, degree);
            c.Graph.Root.Freeze();
            var schema = c.Graph.Root.CreateChild(c.Schema, degree > 1 ? new ScopeOptions { DegreeOfParallelism = degree } : null);
            ForceParallel(schema, degree);
            DiscriminatedSchema.Declare(c, schema);
            schema.Run(Schedule.Saturate(c.Horn, c.Lattice));
            schema.Freeze();
            var facts = new List<IReadOnlyList<string>> { ScopeFacts.Of(schema) };
            foreach (var term in DiscriminatedSchema.Queries(c))
            {
                var query = schema.CreateChild(c.Query, degree > 1 ? new ScopeOptions { DegreeOfParallelism = degree } : null);
                ForceParallel(query, degree);
                var q = query.Make(term);
                query.Assume(c.WhereClause.Of(q));
                query.Run(Schedule.Saturate(c.Horn, c.Lattice, c.Rewrite));
                using (query)
                    facts.Add(ScopeFacts.Of(query));
            }

            return facts;
        }

        /// <summary>
        /// Makes a scope's rule sets run in parallel whatever their size, so that the small predicate programs take the
        /// parallel paths.
        /// </summary>
        static void ForceParallel(Scope scope, int degree)
        {
            if (degree > 1)
                scope.Engine.Context.Context.Database.ParallelRuleSetsOverride = true;
        }

        [Fact]
        public void TheDiscriminatedSchemaAndItsQueriesHaveTheSerialFacts()
        {
            var serial = DiscriminatedSchemaFacts(1);
            var parallel = DiscriminatedSchemaFacts(Degree);

            Assert.Equal(serial.Count, parallel.Count);
            for (var i = 0; i < serial.Count; i++)
                Assert.Equal(serial[i], parallel[i]);

            Assert.Contains(serial[0], line => line.StartsWith("(form \"uid\"", System.StringComparison.Ordinal));
        }

        [Fact]
        public void EachQueryCaseHasTheSerialFacts()
        {
            // Every query that runs without a split; the discriminated schema's are the test above's.
            foreach (var queryCase in ScopedQueryCases.All)
            {
                var serial = Query(1, queryCase.Declare, queryCase.Query);
                var parallel = Query(Degree, queryCase.Declare, queryCase.Query);
                Assert.Equal(serial, parallel);
                Assert.NotEmpty(serial);
            }
        }

        /// <summary>
        /// Compiles a schema and asks one query at the given degree of parallelism; returns the query's facts.
        /// </summary>
        static IReadOnlyList<string> Query(int degree, System.Action<PredicateTheory, Scope> declare, System.Func<PredicateTheory, ConstructorTerm<Pred>> term)
        {
            var c = new PredicateTheory(new EGraph(new EGraphOptions { DegreeOfParallelism = degree }));
            ForceParallel(c.Graph.Root, degree);
            c.Graph.Root.Freeze();
            var schema = c.Graph.Root.CreateChild(c.Schema, degree > 1 ? new ScopeOptions { DegreeOfParallelism = degree } : null);
            ForceParallel(schema, degree);
            declare(c, schema);
            schema.Run(Schedule.Saturate(c.Horn, c.Lattice));
            schema.Freeze();
            using var query = schema.CreateChild(c.Query, degree > 1 ? new ScopeOptions { DegreeOfParallelism = degree } : null);
            ForceParallel(query, degree);
            var q = query.Make(term(c));
            query.Assume(c.WhereClause.Of(q));
            query.Run(Schedule.Saturate(c.Horn, c.Lattice, c.Rewrite));
            return ScopeFacts.Of(query).Concat(ScopeFacts.Of(schema)).ToArray();
        }

    }

}

using System.Collections.Generic;
using System.Linq;

using Eggnog.Tests.Cosmos;
using Eggnog.Tests.Cosmos.Scoped;
using Eggnog.Tests.Support;

using Xunit;

namespace Eggnog.Tests.Concurrency
{

    /// <summary>
    /// Parallel runs are equivalent to serial ones (D46, decision 4): every R2 query of the scoped Cosmos programs
    /// (<see cref="ScopedScenarioCases"/> and sweep's), with a degree of parallelism of 4 on the graph and on the query
    /// scopes, gives every scope the serial run's facts modulo renaming of classes. Extraction is compared at 1 only,
    /// since ties may break differently in parallel.
    /// </summary>
    public sealed class ParallelEquivalenceTests
    {

        /// <summary>
        /// The degree of parallelism of the parallel runs.
        /// </summary>
        const int Degree = 4;

        /// <summary>
        /// Runs sweep's container and its queries; returns the schema's facts and each query's.
        /// </summary>
        static List<IReadOnlyList<string>> SweepFacts(int degree)
        {
            var c = new CosmosTheory(new EGraph(new EGraphOptions { DegreeOfParallelism = degree }));
            ForceParallel(c.Graph.Root, degree);
            c.Graph.Root.Freeze();
            var schema = c.Graph.Root.CreateChild(c.Schema, degree > 1 ? new ScopeOptions { DegreeOfParallelism = degree } : null);
            ForceParallel(schema, degree);
            SweepLinks.Declare(c, schema);
            schema.Run(Schedule.Saturate(c.Horn, c.Lattice));
            schema.Freeze();
            var facts = new List<IReadOnlyList<string>> { ScopeFacts.Of(schema) };
            foreach (var term in SweepLinks.Queries(c))
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
        /// Makes a scope's rule sets run in parallel whatever their size, so that the small Cosmos programs take the
        /// parallel paths.
        /// </summary>
        static void ForceParallel(Scope scope, int degree)
        {
            if (degree > 1)
                scope.Engine.Context.Context.Database.ParallelRuleSetsOverride = true;
        }

        [Fact]
        public void SweepsContainerAndQueriesHaveTheSerialFacts()
        {
            var serial = SweepFacts(1);
            var parallel = SweepFacts(Degree);

            Assert.Equal(serial.Count, parallel.Count);
            for (var i = 0; i < serial.Count; i++)
                Assert.Equal(serial[i], parallel[i]);

            Assert.Contains(serial[0], line => line.StartsWith("(form \"data.guid\"", System.StringComparison.Ordinal));
        }

        [Fact]
        public void EachScenarioHasTheSerialFacts()
        {
            // Every R2 query of scenarios 01 to 06 and 08; scenario 07's are the sweep test's.
            foreach (var scenario in ScopedScenarioCases.All)
            {
                var serial = Query(1, scenario.Declare, scenario.Query);
                var parallel = Query(Degree, scenario.Declare, scenario.Query);
                Assert.Equal(serial, parallel);
                Assert.NotEmpty(serial);
            }
        }

        /// <summary>
        /// Compiles a container and asks one query at the given degree of parallelism; returns the query's facts.
        /// </summary>
        static IReadOnlyList<string> Query(int degree, System.Action<CosmosTheory, Scope> declare, System.Func<CosmosTheory, ConstructorTerm<Pred>> term)
        {
            var c = new CosmosTheory(new EGraph(new EGraphOptions { DegreeOfParallelism = degree }));
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

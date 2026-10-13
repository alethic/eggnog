using System;
using System.Linq;

using Xunit;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// Model-based tests: random queries planned with every strategy, with and without decomposition, evaluated by the
    /// reference <see cref="PlanInterpreter"/> (in static order, and reordered by <see cref="StageOrdering"/> at every
    /// depth as the executor may) must give exactly the tuples of a nested-loop join.
    /// </summary>
    /// <remarks>
    /// The interpreter also fails if a stage reads a variable no earlier stage bound, or if an output variable is
    /// unbound at a leaf, so these tests check plan well-formedness as well as results.
    /// </remarks>
    public class PlannerDifferentialTests
    {

        static readonly PlanStrategy[] Strategies = [PlanStrategy.Gj, PlanStrategy.PureSize, PlanStrategy.MinCover];

        [Theory]
        [InlineData(0)]
        [InlineData(1)]
        [InlineData(2)]
        [InlineData(3)]
        public void SmallQueriesAgreeWithNestedLoopJoin(int shard)
        {
            var decomposed = Run(shard * 100_000, 600, rng => RandomQueries.Generate(rng));
            Assert.True(decomposed > 100, $"the generator should produce decomposed plans, got {decomposed}");
        }

        [Theory]
        [InlineData(0)]
        [InlineData(1)]
        public void LargerQueriesAgreeWithNestedLoopJoin(int shard)
        {
            var decomposed = Run(1_000_000 + shard * 100_000, 150, rng => RandomQueries.Generate(rng, maxAtoms: 9, maxVars: 8, maxArity: 4, prefixKeyOdds: 2, maxRows: 6));
            Assert.True(decomposed > 50, $"the generator should produce decomposed plans, got {decomposed}");
        }

        static int Run(int firstSeed, int count, Func<Random, (PlanTestDatabase Db, Query Query)> generate)
        {
            var decomposed = 0;
            for (var seed = firstSeed; seed < firstSeed + count; seed++)
            {
                var (db, query) = generate(new Random(seed));
                var output = query.GetUsedVariables();
                var expected = NaiveJoin.Evaluate(db, query, output);
                foreach (var strategy in Strategies)
                {
                    foreach (var noDecomposition in new[] { false, true })
                    {
                        var plan = QueryPlanner.PlanQuery(query, db, strategy, noDecomposition);
                        if (plan is DecomposedPlan)
                            decomposed++;

                        foreach (var reorder in new[] { false, true })
                        {
                            var actual = new PlanInterpreter(db, plan, reorder).Run(output);
                            if (actual.SetEquals(expected) == false)
                                Assert.Fail($"seed {seed} strategy {strategy} noDecomposition {noDecomposition} reorder {reorder}: expected {expected.Count} [{string.Join(" ", expected.Order())}] got {actual.Count} [{string.Join(" ", actual.Order())}]\n{plan}");
                        }
                    }
                }
            }

            return decomposed;
        }

    }

}

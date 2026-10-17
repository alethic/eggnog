using System;
using System.Runtime.CompilerServices;

using Xunit;

namespace Eggnog.Tests.Scopes
{

    /// <summary>
    /// What an idle local rule costs a query: the transient child of a frozen container runs every local rule, and a
    /// rule whose body reads nothing new must cost little per iteration. Its semi-naive variants are dropped on their
    /// first header without allocating, and the run's bookkeeping for it is an entry in the run's statistics, in the
    /// child's progress and in the iteration's rule list. The workload is <see cref="IdleRuleWorkload"/>.
    /// </summary>
    [Collection(Relations.Tests.AllocationProbeCollection.Name)]
    public sealed class IdleRuleAllocationTests
    {

        /// <summary>
        /// The bound on the bytes a query allocates per idle local rule per iteration, above a query without rules. Each
        /// rule has two body atoms. The run's statistics (an entry and a <see cref="RuleStatistics"/> in the report) and
        /// the child's progress map take about 210 bytes, and the iteration's rule list, merged and lowered afresh per
        /// iteration, about 220 more, so the bound leaves room for runtime differences and fails when a header
        /// instantiation allocates before it finds its first header empty (about 900 bytes more). A progress map grown
        /// by doubling adds less than the margin; its own test is <see cref="AQueryChildSizesItsProgressMapOnce"/>.
        /// </summary>
        const double BytesPerIdleRule = 600;

        /// <summary>
        /// With <paramref name="layered"/>, the tables the rules read lie in two layers of the query (the root's and the
        /// container's), so each empty header is evaluated per layer.
        /// </summary>
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void AnIdleLocalRuleAllocatesLittlePerIteration(bool layered)
        {
            var none = new IdleRuleWorkload(0, layered);
            var many = new IdleRuleWorkload(200, layered);
            var baseline = BytesPerQuery(none, false, out _);
            var bytes = BytesPerQuery(many, false, out var iterations);

            Assert.Equal(1, iterations);
            var perRule = (bytes - baseline) / many.RuleCount / iterations;
            Assert.True(perRule < BytesPerIdleRule, $"{perRule:0} bytes per idle rule per iteration ({bytes:0} bytes per query with {many.RuleCount} rules, {baseline:0} without)");
        }

        /// <summary>
        /// A child's first run commits progress for every rule, its second for none new, so what the first allocates
        /// beyond the second is the progress map: one pair of arrays at the final capacity, 512 slots for 200 rules at
        /// most half full. Growing by doubling from 8 slots would allocate about twice that.
        /// </summary>
        [Fact]
        public void AQueryChildSizesItsProgressMapOnce()
        {
            var w = new IdleRuleWorkload(200);
            for (var i = 0; i < 5; i++)
                w.BytesPerRun();

            var (first, second) = w.BytesPerRun();
            var map = 512 * (sizeof(int) + Unsafe.SizeOf<Relations.RuleProgress>());
            Assert.True(first - second < 1.5 * map, $"the first run allocates {first - second} bytes more than the second; the map is {map}");
        }

        [Fact]
        public void AnActiveQueryStillDerivesThroughTheLocalRules()
        {
            var w = new IdleRuleWorkload(20);
            var (iterations, held) = w.Query(true);
            Assert.Equal(2, iterations);
            Assert.True(held);

            (iterations, held) = w.Query(false);
            Assert.Equal(1, iterations);
            Assert.True(held);
        }

        /// <summary>
        /// Measures the bytes the current thread allocates per query, after warm-up queries; the graph is serial, so
        /// the query allocates on this thread only.
        /// </summary>
        static double BytesPerQuery(IdleRuleWorkload workload, bool active, out int iterations)
        {
            iterations = 0;
            for (var i = 0; i < 20; i++)
                iterations = workload.Query(active).Iterations;

            const int Queries = 100;
            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var i = 0; i < Queries; i++)
                workload.Query(active);

            return (GC.GetAllocatedBytesForCurrentThread() - before) / (double)Queries;
        }

    }

}

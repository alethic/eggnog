using BenchmarkDotNet.Attributes;

namespace Eggnog.Relations.Benchmarks
{

    /// <summary>
    /// The serial baseline of the find-only workload: egglog's <c>find_only::best_serial</c> bench in
    /// <c>union-find/benches/union-find.rs</c> (<c>cargo bench -p egglog-union-find</c>). Kept apart from
    /// <see cref="UnionFindFindOnlyBenchmarks"/> because it takes none of its parameters.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The workload (egglog's <c>find_only_random</c> over 1,000,000 identifiers) is generated once from a fixed seed;
    /// before each invocation a fresh <see cref="UnionFind"/> reserves identifiers up to 1,000,000 and applies the seed
    /// operations. The invocation calls <see cref="UnionFind.Find"/>, which halves paths, on every identifier in order.
    /// </para>
    /// <para>
    /// The class is not sealed because BenchmarkDotNet generates a class that derives from it.
    /// </para>
    /// </remarks>
    [InvocationCount(1)]
    public class UnionFindFindOnlySerialBenchmarks : IItemsPerInvocation
    {

        FindOnlyWorkload? workload;
        UnionFind? unionFind;

        /// <summary>
        /// Generates the workload.
        /// </summary>
        [GlobalSetup]
        public void GlobalSetup()
        {
            workload = FindOnlyWorkload.PrepareRandom(UnionFindFindOnlyBenchmarks.ItemCount, UnionFindFindOnlyBenchmarks.Seed);
        }

        /// <summary>
        /// Builds the union-find the next invocation runs on.
        /// </summary>
        [IterationSetup]
        public void IterationSetup()
        {
            var fresh = new UnionFind();
            fresh.Find(UnionFindFindOnlyBenchmarks.ItemCount);
            UnionFindWorkload.Apply(fresh, workload!.SeedOperations);
            unionFind = fresh;
        }

        /// <summary>
        /// Releases the union-find.
        /// </summary>
        [IterationCleanup]
        public void IterationCleanup()
        {
            unionFind = null;
        }

        /// <summary>
        /// egglog's <c>best_serial</c>: <c>uf.find(op)</c> for every identifier, on one thread.
        /// </summary>
        [Benchmark]
        public void BestSerial()
        {
            var target = unionFind!;
            foreach (var id in workload!.ToFind)
                target.Find(id);
        }

        /// <inheritdoc />
        long IItemsPerInvocation.ItemsPerInvocation(string benchmarkMethod) => 2L * UnionFindFindOnlyBenchmarks.ItemCount;

    }

}

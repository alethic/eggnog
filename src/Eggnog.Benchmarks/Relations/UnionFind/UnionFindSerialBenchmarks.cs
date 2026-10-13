using BenchmarkDotNet.Attributes;

namespace Eggnog.Relations.Benchmarks
{

    /// <summary>
    /// The serial <see cref="UnionFind"/> on the "random" and "local" workloads: egglog's <c>random::serial</c> and
    /// <c>local::serial</c> benches (<c>uf_serial</c>) in <c>union-find/benches/union-find.rs</c>
    /// (<c>cargo bench -p egglog-union-find</c>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Before each invocation a fresh union-find reserves identifiers up to <see cref="N"/> (egglog's
    /// <c>uf.find(n_items)</c>) and applies the seed operations; the invocation applies the measured operations in
    /// order. The workload is generated once per parameter set from a fixed seed.
    /// </para>
    /// <para>
    /// The class is not sealed because BenchmarkDotNet generates a class that derives from it.
    /// </para>
    /// </remarks>
    [InvocationCount(1)]
    public class UnionFindSerialBenchmarks : IItemsPerInvocation
    {

        /// <summary>
        /// The seed every workload is generated from.
        /// </summary>
        public const int Seed = 0x5EED;

        UnionFindWorkload? workload;
        UnionFind? unionFind;

        /// <summary>
        /// Gets or sets the workload shape: egglog's bench group (<c>random</c> or <c>local</c>).
        /// </summary>
        [Params(UnionFindWorkloadKind.Random, UnionFindWorkloadKind.Local)]
        public UnionFindWorkloadKind Workload { get; set; }

        /// <summary>
        /// Gets or sets the number of identifiers: egglog's <c>LENGTHS</c>.
        /// </summary>
        [Params(1000, 100000, 1000000, 4000000)]
        public int N { get; set; }

        /// <summary>
        /// Generates the workload.
        /// </summary>
        [GlobalSetup]
        public void GlobalSetup()
        {
            workload = UnionFindWorkload.Prepare(Workload, N, Seed);
        }

        /// <summary>
        /// Builds the union-find the next invocation runs on (egglog's <c>with_inputs</c> closure).
        /// </summary>
        [IterationSetup]
        public void IterationSetup()
        {
            var fresh = new UnionFind();
            fresh.Find((uint)N);
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
        /// egglog's <c>uf_serial</c>: every measured operation, in order, on one thread.
        /// </summary>
        [Benchmark]
        public void Serial()
        {
            UnionFindWorkload.Apply(unionFind!, workload!.Operations);
        }

        /// <inheritdoc />
        long IItemsPerInvocation.ItemsPerInvocation(string benchmarkMethod) => UnionFindWorkload.OperationCount(Workload, N, Seed);

    }

}

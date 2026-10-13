using System;
using System.Threading.Tasks;

using BenchmarkDotNet.Attributes;

namespace Eggnog.Relations.Benchmarks
{

    /// <summary>
    /// The <see cref="ConcurrentUnionFind"/> driven by work-stealing tasks of 4096 operations on the "random" and
    /// "local" workloads: egglog's <c>rayon_work_stealing::random</c> and <c>rayon_work_stealing::local</c> benches
    /// (<c>run_using_rayon</c>) in <c>union-find/benches/union-find.rs</c> (<c>cargo bench -p egglog-union-find</c>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// egglog spawns one task per chunk of 4096 operations into a <c>rayon::scope</c> on the global pool (one thread per
    /// logical core); this runs one <see cref="Parallel.For(int, int, ParallelOptions, Action{int})"/> iteration per
    /// chunk, capped at <see cref="Environment.ProcessorCount"/> threads. Before each invocation a fresh union-find with
    /// capacity <see cref="N"/> applies the seed operations. The workload is generated once per parameter set from a
    /// fixed seed, and every identifier's root is checked against a serial reference after each iteration.
    /// </para>
    /// <para>
    /// The class is not sealed because BenchmarkDotNet generates a class that derives from it.
    /// </para>
    /// </remarks>
    [InvocationCount(1)]
    public class UnionFindWorkStealingBenchmarks : IItemsPerInvocation
    {

        /// <summary>
        /// The operations per task (egglog's <c>chunk_size</c>).
        /// </summary>
        public const int ChunkSize = 4096;

        /// <summary>
        /// The seed every workload is generated from.
        /// </summary>
        public const int Seed = 0x5EED;

        /// <summary>
        /// The counterpart of rayon's global pool: one worker per logical core.
        /// </summary>
        static readonly ParallelOptions GlobalPool = new() { MaxDegreeOfParallelism = Environment.ProcessorCount };

        UnionFindWorkload? workload;
        uint[]? expectedRoots;
        ConcurrentUnionFind? unionFind;
        bool ran;

        /// <summary>
        /// Gets or sets the workload shape: egglog's bench (<c>random</c> or <c>local</c>).
        /// </summary>
        [Params(UnionFindWorkloadKind.Random, UnionFindWorkloadKind.Local)]
        public UnionFindWorkloadKind Workload { get; set; }

        /// <summary>
        /// Gets or sets the number of identifiers: egglog's <c>LENGTHS</c>.
        /// </summary>
        [Params(1000, 100000, 1000000, 4000000)]
        public int N { get; set; }

        /// <summary>
        /// Generates the workload and computes the roots a correct run ends with.
        /// </summary>
        [GlobalSetup]
        public void GlobalSetup()
        {
            workload = UnionFindWorkload.Prepare(Workload, N, Seed);
            expectedRoots = UnionFindWorkload.ReferenceRoots(N, workload.SeedOperations, workload.Operations);
        }

        /// <summary>
        /// Builds the union-find the next invocation runs on.
        /// </summary>
        [IterationSetup]
        public void IterationSetup()
        {
            var fresh = new ConcurrentUnionFind(N);
            UnionFindWorkload.Apply(fresh, workload!.SeedOperations);
            unionFind = fresh;
        }

        /// <summary>
        /// Checks the final roots and releases the union-find. An iteration that did not run the benchmark leaves a
        /// union-find that only saw the seed, so the check is skipped then.
        /// </summary>
        /// <exception cref="InvalidOperationException">An identifier ended with the wrong root.</exception>
        [IterationCleanup]
        public void IterationCleanup()
        {
            var finished = unionFind!;
            unionFind = null;
            if (ran)
                UnionFindWorkload.VerifyRoots(finished, expectedRoots!);

            ran = false;
        }

        /// <summary>
        /// egglog's <c>run_using_rayon</c> measured body: every chunk of measured operations as a work-stealing task.
        /// </summary>
        [Benchmark]
        public void WorkStealing()
        {
            var target = unionFind!;
            var operations = workload!.Operations;
            var chunks = (operations.Length + ChunkSize - 1) / ChunkSize;
            Parallel.For(0, chunks, GlobalPool, chunk =>
            {
                var from = chunk * ChunkSize;
                UnionFindWorkload.Apply(target, operations.AsSpan(from, Math.Min(ChunkSize, operations.Length - from)));
            });

            ran = true;
        }

        /// <inheritdoc />
        long IItemsPerInvocation.ItemsPerInvocation(string benchmarkMethod) => UnionFindWorkload.OperationCount(Workload, N, Seed);

    }

}

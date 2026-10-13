using System;
using System.Threading;

using BenchmarkDotNet.Attributes;

namespace Eggnog.Relations.Benchmarks
{

    /// <summary>
    /// The <see cref="ConcurrentUnionFind"/> driven by a fixed number of dedicated threads on the "random" and "local"
    /// workloads: egglog's <c>random::concurrent_{N}threads</c> and <c>local::concurrent_{N}threads</c> benches
    /// (<c>uf_parallel</c> with <c>concurrent::UnionFind</c>) in <c>union-find/benches/union-find.rs</c>
    /// (<c>cargo bench -p egglog-union-find</c>). egglog's <c>Arc&lt;Mutex&lt;UnionFind&gt;&gt;</c> baseline is not
    /// ported.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Before each invocation, as in egglog's <c>with_inputs</c>: a fresh union-find with capacity <see cref="N"/>
    /// applies the seed operations; the measured operations are truncated to a multiple of <see cref="Threads"/> and
    /// split into one contiguous chunk per thread; and one thread per chunk is started, blocked on a
    /// <see cref="Notification"/>. The invocation notifies and joins them, so thread creation is not measured but
    /// waking and joining are, as in egglog. The workload is generated once per parameter set from a fixed seed.
    /// </para>
    /// <para>
    /// After each iteration every identifier's root is checked against a serial reference: both union-finds link by
    /// minimum identifier, so the final roots do not depend on the interleaving.
    /// </para>
    /// <para>
    /// The class is not sealed because BenchmarkDotNet generates a class that derives from it.
    /// </para>
    /// </remarks>
    [InvocationCount(1)]
    public class UnionFindConcurrentBenchmarks : IItemsPerInvocation
    {

        /// <summary>
        /// The seed every workload is generated from.
        /// </summary>
        public const int Seed = 0x5EED;

        UnionFindWorkload? workload;
        int operationCount;
        uint[]? expectedRoots;
        ConcurrentUnionFind? unionFind;
        Notification? start;
        Thread[]? workers;

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
        /// Gets or sets the number of threads: egglog's <c>concurrent_{N}threads</c> (1, 2, 4, 8 and 16 there; this
        /// machine stops at its 8 logical cores).
        /// </summary>
        [Params(1, 2, 4, 8)]
        public int Threads { get; set; }

        /// <summary>
        /// Generates the workload, truncates it to a multiple of <see cref="Threads"/>, and computes the roots a correct
        /// run ends with.
        /// </summary>
        [GlobalSetup]
        public void GlobalSetup()
        {
            workload = UnionFindWorkload.Prepare(Workload, N, Seed);
            operationCount = TruncatedCount(workload.Operations.Length, Threads);
            expectedRoots = UnionFindWorkload.ReferenceRoots(N, workload.SeedOperations, workload.Operations.AsSpan(0, operationCount));
        }

        /// <summary>
        /// Builds the union-find and starts the blocked worker threads the next invocation releases.
        /// </summary>
        [IterationSetup]
        public void IterationSetup()
        {
            var operations = workload!.Operations;
            var fresh = new ConcurrentUnionFind(N);
            UnionFindWorkload.Apply(fresh, workload.SeedOperations);

            var notification = new Notification();
            var chunk = operationCount / Threads;
            var threads = new Thread[Threads];
            for (var t = 0; t < threads.Length; t++)
            {
                var from = t * chunk;
                threads[t] = new Thread(() =>
                {
                    notification.Wait();
                    UnionFindWorkload.Apply(fresh, operations.AsSpan(from, chunk));
                });
                threads[t].IsBackground = true;
                threads[t].Start();
            }

            unionFind = fresh;
            start = notification;
            workers = threads;
        }

        /// <summary>
        /// Makes sure every worker finished (an iteration that did not run the benchmark still started them), checks the
        /// final roots, and releases the union-find.
        /// </summary>
        /// <exception cref="InvalidOperationException">An identifier ended with the wrong root.</exception>
        [IterationCleanup]
        public void IterationCleanup()
        {
            start!.Notify();
            foreach (var worker in workers!)
                worker.Join();

            var finished = unionFind!;
            unionFind = null;
            start = null;
            workers = null;
            UnionFindWorkload.VerifyRoots(finished, expectedRoots!);
        }

        /// <summary>
        /// egglog's <c>uf_parallel</c> measured body: notify the workers and join them.
        /// </summary>
        [Benchmark]
        public void Concurrent()
        {
            start!.Notify();
            foreach (var worker in workers!)
                worker.Join();
        }

        /// <inheritdoc />
        long IItemsPerInvocation.ItemsPerInvocation(string benchmarkMethod)
        {
            return TruncatedCount(UnionFindWorkload.OperationCount(Workload, N, Seed), Threads);
        }

        /// <summary>
        /// Gets the number of operations egglog's <c>uf_parallel</c> keeps: <c>operations.truncate(chunk_size *
        /// n_threads)</c> with <c>chunk_size = operations.len() / n_threads</c>.
        /// </summary>
        /// <param name="count">The number of generated operations.</param>
        /// <param name="threads">The number of threads.</param>
        /// <returns>The number of operations measured.</returns>
        static int TruncatedCount(int count, int threads) => count / threads * threads;

    }

}

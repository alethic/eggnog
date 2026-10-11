using System;
using System.Threading.Tasks;

using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Engines;

namespace Eggnog.Relations.Benchmarks
{

    /// <summary>
    /// Finds from parallel tasks with no concurrent unions: egglog's <c>find_only::parallel_naive</c> bench
    /// (<c>naive_find</c>) in <c>union-find/benches/union-find.rs</c> (<c>cargo bench -p egglog-union-find</c>), for the
    /// two union-finds this engine has: <c>concurrent::UnionFind</c> (<see cref="ConcurrentUnionFind"/>, whose finds
    /// split paths) and <c>Arc&lt;ReadOptimizedLock&lt;UnionFind&gt;&gt;</c> (a <see cref="UnionFind"/> behind a
    /// <see cref="ReadOptimizedLock{T}"/>, each task taking one read lock and calling
    /// <see cref="UnionFind.FindNaive"/>). egglog's <c>Arc&lt;RwLock&lt;UnionFind&gt;&gt;</c> baseline is not ported.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The workload (egglog's <c>find_only_random</c> over 1,000,000 identifiers: 250,000 seeded unions, then 2,000,000
    /// identifiers to find) is generated once from a fixed seed; before each invocation a fresh union-find applies the
    /// seed operations. egglog spawns one task per chunk of <see cref="BatchSize"/> identifiers into a
    /// <c>rayon::scope</c> on the global pool; this runs one
    /// <see cref="Parallel.For(int, int, ParallelOptions, Action{int})"/> iteration per chunk, capped at
    /// <see cref="Environment.ProcessorCount"/> threads. Each task folds its results into one value it keeps alive, in
    /// place of egglog's <c>black_box</c> per find.
    /// </para>
    /// <para>
    /// After each iteration every identifier's root is checked against a serial reference.
    /// </para>
    /// <para>
    /// The class is not sealed because BenchmarkDotNet generates a class that derives from it.
    /// </para>
    /// </remarks>
    [InvocationCount(1)]
    public class UnionFindFindOnlyBenchmarks : IItemsPerInvocation
    {

        /// <summary>
        /// The number of identifiers (egglog's <c>find_only::N_ITEMS</c>).
        /// </summary>
        public const int ItemCount = 1_000_000;

        /// <summary>
        /// The seed the workload is generated from.
        /// </summary>
        public const int Seed = 0x5EED;

        /// <summary>
        /// The counterpart of rayon's global pool: one worker per logical core.
        /// </summary>
        static readonly ParallelOptions GlobalPool = new() { MaxDegreeOfParallelism = Environment.ProcessorCount };

        FindOnlyWorkload? workload;
        uint[]? expectedRoots;
        ConcurrentUnionFind? concurrent;
        ReadOptimizedLock<UnionFind>? locked;

        /// <summary>
        /// Gets or sets the number of identifiers each task finds: egglog's const parameter <c>N</c>.
        /// </summary>
        [Params(32, 128, 256, 1024)]
        public int BatchSize { get; set; }

        /// <summary>
        /// Generates the workload and computes the roots of every identifier.
        /// </summary>
        [GlobalSetup]
        public void GlobalSetup()
        {
            workload = FindOnlyWorkload.PrepareRandom(ItemCount, Seed);
            expectedRoots = UnionFindWorkload.ReferenceRoots(ItemCount, workload.SeedOperations, ReadOnlySpan<UnionFindOperation>.Empty);
        }

        /// <summary>
        /// Builds the concurrent union-find the next invocation of <see cref="ParallelNaiveConcurrent"/> runs on
        /// (egglog's <c>UF::with_capacity(N_ITEMS)</c> and <c>apply_ops</c>).
        /// </summary>
        [IterationSetup(Target = nameof(ParallelNaiveConcurrent))]
        public void SetupConcurrent()
        {
            var fresh = new ConcurrentUnionFind(ItemCount);
            UnionFindWorkload.Apply(fresh, workload!.SeedOperations);
            concurrent = fresh;
        }

        /// <summary>
        /// Builds the locked union-find the next invocation of <see cref="ParallelNaiveReadOptimizedLock"/> runs on:
        /// identifiers reserved up to <see cref="ItemCount"/>, and the seed applied under one write lock (egglog's
        /// <c>apply_ops</c> for this type).
        /// </summary>
        [IterationSetup(Target = nameof(ParallelNaiveReadOptimizedLock))]
        public void SetupReadOptimizedLock()
        {
            var unionFind = new UnionFind();
            unionFind.Find(ItemCount);
            var fresh = new ReadOptimizedLock<UnionFind>(unionFind);
            using (var writer = fresh.Lock())
                UnionFindWorkload.Apply(writer.Value, workload!.SeedOperations);

            locked = fresh;
        }

        /// <summary>
        /// Checks the roots after a <see cref="ParallelNaiveConcurrent"/> iteration and releases the union-find.
        /// </summary>
        /// <exception cref="InvalidOperationException">An identifier has the wrong root.</exception>
        [IterationCleanup(Target = nameof(ParallelNaiveConcurrent))]
        public void CleanupConcurrent()
        {
            var finished = concurrent!;
            concurrent = null;
            UnionFindWorkload.VerifyRoots(finished, expectedRoots!);
        }

        /// <summary>
        /// Checks the roots after a <see cref="ParallelNaiveReadOptimizedLock"/> iteration and releases the union-find.
        /// </summary>
        /// <exception cref="InvalidOperationException">An identifier has the wrong root.</exception>
        [IterationCleanup(Target = nameof(ParallelNaiveReadOptimizedLock))]
        public void CleanupReadOptimizedLock()
        {
            var finished = locked!;
            locked = null;
            UnionFindWorkload.VerifyRoots(finished.IntoInner(), expectedRoots!);
        }

        /// <summary>
        /// egglog's <c>parallel_naive&lt;N, concurrent::UnionFind&lt;usize&gt;&gt;</c>: each task finds its chunk with
        /// <see cref="ConcurrentUnionFind.Find"/> (the trait's default <c>find_naive</c> is <c>find</c>).
        /// </summary>
        [Benchmark]
        public void ParallelNaiveConcurrent()
        {
            var unionFind = concurrent!;
            var ids = workload!.ToFind;
            var batch = BatchSize;
            var chunks = (ids.Length + batch - 1) / batch;
            Parallel.For(0, chunks, GlobalPool, chunk =>
            {
                var from = chunk * batch;
                var to = Math.Min(ids.Length, from + batch);
                var folded = 0u;
                for (var i = from; i < to; i++)
                    folded ^= unionFind.Find(ids[i]);

                DeadCodeEliminationHelper.KeepAliveWithoutBoxing(folded);
            });
        }

        /// <summary>
        /// egglog's <c>parallel_naive&lt;N, Arc&lt;ReadOptimizedLock&lt;UnionFind&lt;usize&gt;&gt;&gt;&gt;</c>: each task takes
        /// one read lock and finds its chunk with <see cref="UnionFind.FindNaive"/>.
        /// </summary>
        [Benchmark]
        public void ParallelNaiveReadOptimizedLock()
        {
            var guarded = locked!;
            var ids = workload!.ToFind;
            var batch = BatchSize;
            var chunks = (ids.Length + batch - 1) / batch;
            Parallel.For(0, chunks, GlobalPool, chunk =>
            {
                var from = chunk * batch;
                var to = Math.Min(ids.Length, from + batch);
                var folded = 0u;
                using (var reader = guarded.Read())
                {
                    var unionFind = reader.Value;
                    for (var i = from; i < to; i++)
                        folded ^= unionFind.FindNaive(ids[i]);
                }

                DeadCodeEliminationHelper.KeepAliveWithoutBoxing(folded);
            });
        }

        /// <inheritdoc />
        long IItemsPerInvocation.ItemsPerInvocation(string benchmarkMethod) => 2L * ItemCount;

    }

}

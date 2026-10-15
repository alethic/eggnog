using System;
using System.Threading.Tasks;

using BenchmarkDotNet.Attributes;

using Eggnog.Benchmarks.Probes;

namespace Eggnog.Relations.Benchmarks
{

    /// <summary>
    /// Staging and merging writes into a <see cref="SortedWritesTable"/> from parallel workers: a port of egglog's
    /// <c>core-relations/benches/writes.rs</c> (<c>cargo bench -p egglog-core-relations --bench writes</c>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Each benchmark runs egglog's <c>bench_workload</c>: the workload is split into <c>n_merges</c> epochs; in each
    /// epoch, batches of <see cref="BatchSize"/> operations are staged by parallel workers, each batch into its own
    /// fresh <see cref="MutationBuffer"/> that is flushed when the batch is done (egglog's
    /// <c>outer.chunks(BATCH_SIZE).par_bridge().for_each(|batch| { let mut buf = table.new_buffer(); .. })</c>, where
    /// dropping the buffer flushes it), and then the table merges once. The table has 3 key columns of 5, no sort column,
    /// nothing to rebuild, and <see cref="ReplaceRowMerge"/>.
    /// </para>
    /// <para>
    /// <see cref="Threads"/> plays the size of egglog's <c>ThreadPoolBuilder</c> pool that the bench <c>install</c>s:
    /// it caps both the staging workers and the table's own parallel delete, insert and compaction, through one
    /// <see cref="ParallelHeuristics"/>. That instance also fixes the table's shard count: by default twice the thread
    /// count, or one shard serially. egglog builds the table outside <c>install</c>, so its shard count follows the
    /// global rayon pool instead (twice the machine's threads, 16 on an eight-thread machine);
    /// <see cref="Shards"/> = 16 reproduces that at every thread count. At 8 threads the default is already 16 shards,
    /// so that case is left out (<see cref="DistinctShardCountsAttribute"/>).
    /// </para>
    /// <para>
    /// The workload is generated once per parameter set from a fixed seed (the global setup runs once per benchmark
    /// case); a fresh database and table are built before every invocation (<see cref="IterationSetup"/>, one
    /// invocation per iteration), as divan's <c>with_inputs</c> does. After each iteration the table's live row count
    /// is checked against a reference model.
    /// </para>
    /// <para>
    /// The class is not sealed because BenchmarkDotNet generates a class that derives from it.
    /// </para>
    /// </remarks>
    [InvocationCount(1)]
    [DistinctShardCounts]
    public class WritesBenchmarks : IItemsPerInvocation, IScheduledBenchmark
    {

        /// <summary>
        /// The operations staged into one buffer (egglog's <c>BATCH_SIZE</c>).
        /// </summary>
        public const int BatchSize = 1024;

        /// <summary>
        /// The workload size of <see cref="ParallelInsert"/> and <see cref="ParallelInsertMerge2"/> (<c>4 &lt;&lt; 20</c>).
        /// </summary>
        public const int InsertWorkloadSize = 4 << 20;

        /// <summary>
        /// The workload size of <see cref="ParallelInsertRemoveWithCollisions"/> (<c>1 &lt;&lt; 20</c>).
        /// </summary>
        public const int RemoveWorkloadSize = 1 << 20;

        /// <summary>
        /// The seed every workload is generated from.
        /// </summary>
        public const int Seed = 0x5EED;

        readonly ReplaceRowMerge merge = new();
        WritesWorkload? workload;
        ParallelHeuristics? parallelism;
        int merges;
        int expectedCount;
        Database? database;
        SortedWritesTable? table;
        TaskScheduler? scheduler;
        bool ran;

        /// <summary>
        /// Gets or sets the number of threads: egglog's const parameter <c>N</c> (1, 2, 4, 8 and 16 there; this machine
        /// stops at its 8 logical cores).
        /// </summary>
        [Params(1, 2, 4, 8)]
        public int Threads { get; set; }

        /// <summary>
        /// Gets or sets the shard count of the table's hash index: 0, the default, takes twice <see cref="Threads"/>
        /// (one shard at one thread), as <see cref="ParallelHeuristics"/> derives it; 16 fixes 16 shards whatever the
        /// thread count, as egglog's bench gets on an eight-thread machine. A case of 16 whose thread count already
        /// gives 16 shards is not run (<see cref="WritesShardCounts.IsDistinct"/>).
        /// </summary>
        [Params(0, 16)]
        public int Shards { get; set; }

        /// <summary>
        /// Gets or sets the scheduler the staging loops and the table's parallel operations run on, or <c>null</c> for
        /// the thread pool. Read by the global setup. For <see cref="AllocationProbe"/>'s per-thread count.
        /// </summary>
        TaskScheduler? IScheduledBenchmark.Scheduler
        {
            get => scheduler;
            set => scheduler = value;
        }

        /// <summary>
        /// Generates the workload of <see cref="ParallelInsert"/>: inserts only, 5% of them reusing an earlier key.
        /// </summary>
        [GlobalSetup(Target = nameof(ParallelInsert))]
        public void SetupParallelInsert() => Setup(WritesWorkload.Generate(InsertWorkloadSize, 1.0, 0.05, Seed), 1);

        /// <summary>
        /// Generates the workload of <see cref="ParallelInsertMerge2"/>: that of <see cref="ParallelInsert"/>, merged in
        /// two epochs.
        /// </summary>
        [GlobalSetup(Target = nameof(ParallelInsertMerge2))]
        public void SetupParallelInsertMerge2() => Setup(WritesWorkload.Generate(InsertWorkloadSize, 1.0, 0.05, Seed), 2);

        /// <summary>
        /// Generates the workload of <see cref="ParallelInsertRemoveWithCollisions"/>: 25% removals of earlier keys, and
        /// 15% of the inserts reusing an earlier key.
        /// </summary>
        [GlobalSetup(Target = nameof(ParallelInsertRemoveWithCollisions))]
        public void SetupParallelInsertRemoveWithCollisions() => Setup(WritesWorkload.Generate(RemoveWorkloadSize, 0.75, 0.15, Seed), 1);

        /// <summary>
        /// Builds a fresh database and an empty table for the next invocation (egglog's <c>with_inputs</c> closure), and
        /// checks that the table has the shard count <see cref="Threads"/> and <see cref="Shards"/> call for.
        /// </summary>
        /// <exception cref="InvalidOperationException">The table has another shard count.</exception>
        [IterationSetup]
        public void IterationSetup()
        {
            var heuristics = parallelism!;
            database = new Database(heuristics);
            table = new SortedWritesTable(
                WritesWorkload.KeyCount,
                WritesWorkload.ColumnCount,
                null,
                ReadOnlySpan<ColumnId>.Empty,
                merge,
                heuristics);

            var expected = WritesShardCounts.Expected(Threads, Shards);
            if (table.Parallelism.ShardData.Count != expected)
                throw new InvalidOperationException(
                    $"The table has {table.Parallelism.ShardData.Count} shards; Threads = {Threads} and Shards = {Shards} call for {expected}.");
        }

        /// <summary>
        /// Checks that the invocation left the table with the live rows the reference model predicts, and releases the
        /// table. An iteration that did not run the benchmark is not checked.
        /// </summary>
        /// <exception cref="InvalidOperationException">The table holds the wrong number of rows.</exception>
        [IterationCleanup]
        public void IterationCleanup()
        {
            var count = table!.Count;
            var check = ran;
            table = null;
            database = null;
            ran = false;
            if (check && count != expectedCount)
                throw new InvalidOperationException(
                    $"The table holds {count} live rows after the merge; the reference model predicts {expectedCount}.");
        }

        /// <summary>
        /// egglog's <c>parallel_insert</c>: 4M operations, all inserts with 5% key collisions, one merge.
        /// </summary>
        [Benchmark]
        public void ParallelInsert() => Run();

        /// <summary>
        /// egglog's <c>parallel_insert_merge2</c>: the <see cref="ParallelInsert"/> workload in two epochs, one merge
        /// each.
        /// </summary>
        [Benchmark]
        public void ParallelInsertMerge2() => Run();

        /// <summary>
        /// egglog's <c>parallel_insert_remove_with_collisions</c>: 1M operations, 25% removals of existing keys and 15%
        /// key collisions among the inserts, one merge.
        /// </summary>
        [Benchmark]
        public void ParallelInsertRemoveWithCollisions() => Run();

        /// <inheritdoc />
        long IItemsPerInvocation.ItemsPerInvocation(string benchmarkMethod) => benchmarkMethod switch
        {
            nameof(ParallelInsert) or nameof(ParallelInsertMerge2) => InsertWorkloadSize,
            nameof(ParallelInsertRemoveWithCollisions) => RemoveWorkloadSize,
            _ => 0,
        };

        /// <summary>
        /// Keeps a generated workload and the parallelism for <see cref="Threads"/> and <see cref="Shards"/>, and
        /// computes the live row count a correct merge produces.
        /// </summary>
        /// <param name="generated">The workload.</param>
        /// <param name="mergeCount">The number of epochs it is merged in.</param>
        void Setup(WritesWorkload generated, int mergeCount)
        {
            workload = generated;
            merges = mergeCount;
            parallelism = new ParallelHeuristics(Threads, Shards);
            if (scheduler != null)
                parallelism.Options.TaskScheduler = scheduler;

            expectedCount = generated.CountLiveKeys(mergeCount);
        }

        /// <summary>
        /// The measured body of egglog's <c>bench_workload</c>: for each epoch, stage its batches in parallel, then merge
        /// the table once inside an execution state (<c>db.with_execution_state(|es| table.merge(es))</c>).
        /// </summary>
        void Run()
        {
            var operations = workload!;
            var target = table!;
            var db = database!;
            var options = parallelism!.Options;
            var count = operations.Count;
            var epochSize = operations.EpochSize(merges);
            for (var start = 0; start < count; start += epochSize)
            {
                var epochStart = start;
                var epochEnd = Math.Min(count, start + epochSize);
                var batches = (epochEnd - epochStart + BatchSize - 1) / BatchSize;
                Parallel.For(0, batches, options, batch =>
                {
                    var from = epochStart + batch * BatchSize;
                    StageBatch(operations, target, from, Math.Min(epochEnd, from + BatchSize));
                });

                db.WithExecutionState(target.Merge);
            }

            ran = true;
        }

        /// <summary>
        /// Stages operations [<paramref name="from"/>, <paramref name="to"/>) into a fresh buffer of
        /// <paramref name="table"/> and flushes it, as one iteration of egglog's <c>for_each</c> does.
        /// </summary>
        /// <param name="operations">The workload.</param>
        /// <param name="table">The table.</param>
        /// <param name="from">The first operation.</param>
        /// <param name="to">One past the last operation.</param>
        static void StageBatch(WritesWorkload operations, SortedWritesTable table, int from, int to)
        {
            var buffer = table.NewBuffer();
            var cells = operations.Cells;
            var isRemoval = operations.IsRemoval;
            for (var i = from; i < to; i++)
            {
                var offset = i * WritesWorkload.ColumnCount;
                if (isRemoval[i])
                    buffer.StageRemove(new ReadOnlySpan<uint>(cells, offset, WritesWorkload.KeyCount));
                else
                    buffer.StageInsert(new ReadOnlySpan<uint>(cells, offset, WritesWorkload.ColumnCount));
            }

            buffer.Flush();
        }

    }

}

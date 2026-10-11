using System;
using System.Threading.Tasks;

namespace Eggnog.Relations
{

    /// <summary>
    /// When the engine runs an operation in parallel, and on how many threads: egglog's
    /// <c>parallel_heuristics.rs</c>, with a configured degree of parallelism in place of rayon's pool size.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every heuristic is egglog's size threshold and also requires <see cref="DegreeOfParallelism"/> &gt; 1, as egglog
    /// requires <c>rayon::current_num_threads() &gt; 1</c>. Small inputs stay serial because the parallel algorithms
    /// have a noticeable fixed cost.
    /// </para>
    /// <para>
    /// Tables capture an instance at construction (it fixes their shard count, as the rayon pool size fixes egglog's).
    /// <see cref="Default"/> uses every processor; <see cref="Serial"/> never parallelizes and gives tables a single
    /// shard. Immutable and safe to share.
    /// </para>
    /// </remarks>
    internal sealed class ParallelHeuristics
    {

        /// <summary>
        /// Database-level operations (rule sets, per-table rebuild, merge strata) go parallel above this total size.
        /// egglog's <c>parallelize_db_level_op</c>.
        /// </summary>
        public const int DbLevelThreshold = 10_000;

        /// <summary>
        /// Table inserts, deletes and compactions go parallel above this many rows. egglog's
        /// <c>parallelize_table_op</c>.
        /// </summary>
        public const int TableOpThreshold = 20_000;

        /// <summary>
        /// Hash index construction goes parallel above this many rows. egglog's
        /// <c>parallelize_index_construction</c>.
        /// </summary>
        public const int IndexConstructionThreshold = 20_000;

        /// <summary>
        /// Table rebuilds go parallel above this many rows (or displaced ids). egglog's <c>parallelize_rebuild</c>.
        /// </summary>
        public const int RebuildThreshold = 10_000;

        /// <summary>
        /// Operations within one container memo table go parallel above this many containers. egglog's
        /// <c>parallelize_intra_container_op</c>.
        /// </summary>
        public const int IntraContainerThreshold = 1_000;

        /// <summary>
        /// Operations across container memo tables go parallel above this many tables. egglog's
        /// <c>parallelize_inter_container_op</c>.
        /// </summary>
        public const int InterContainerThreshold = 1;

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="degreeOfParallelism">The number of threads parallel operations may use; one disables them.</param>
        public ParallelHeuristics(int degreeOfParallelism)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(degreeOfParallelism, 1);

            DegreeOfParallelism = degreeOfParallelism;
            Options = new ParallelOptions { MaxDegreeOfParallelism = degreeOfParallelism };
            ShardData = ShardData.ForParallelism(degreeOfParallelism);
        }

        /// <summary>
        /// Gets heuristics that use every processor of the machine.
        /// </summary>
        public static ParallelHeuristics Default { get; } = new(Math.Max(1, Environment.ProcessorCount));

        /// <summary>
        /// Gets heuristics that never parallelize.
        /// </summary>
        public static ParallelHeuristics Serial { get; } = new(1);

        /// <summary>
        /// Gets the number of threads parallel operations may use.
        /// </summary>
        public int DegreeOfParallelism { get; }

        /// <summary>
        /// Gets the options to pass to <see cref="Parallel.For(int, int, ParallelOptions, Action{int})"/>; they cap it
        /// at <see cref="DegreeOfParallelism"/>.
        /// </summary>
        public ParallelOptions Options { get; }

        /// <summary>
        /// Gets the shard layout of a table's hash index: one shard when serial, otherwise twice the degree of
        /// parallelism rounded up to a power of two (egglog's <c>ShardedHashTable::default</c>,
        /// core-relations/src/table/sharded_hash_table.rs:14-23).
        /// </summary>
        public ShardData ShardData { get; }

        /// <summary>
        /// Gets whether a database-level operation over <paramref name="size"/> rows runs in parallel.
        /// </summary>
        /// <param name="size">The total size estimate.</param>
        /// <returns><c>true</c> to run in parallel.</returns>
        public bool ParallelizeDbLevelOp(long size) => size > DbLevelThreshold && DegreeOfParallelism > 1;

        /// <summary>
        /// Gets whether a table insert, delete or compaction over <paramref name="rows"/> rows runs in parallel.
        /// </summary>
        /// <param name="rows">The number of rows.</param>
        /// <returns><c>true</c> to run in parallel.</returns>
        public bool ParallelizeTableOp(long rows) => rows > TableOpThreshold && DegreeOfParallelism > 1;

        /// <summary>
        /// Gets whether building an index over <paramref name="rows"/> rows runs in parallel.
        /// </summary>
        /// <param name="rows">The number of rows to insert.</param>
        /// <returns><c>true</c> to run in parallel.</returns>
        public bool ParallelizeIndexConstruction(long rows) => rows > IndexConstructionThreshold && DegreeOfParallelism > 1;

        /// <summary>
        /// Gets whether a rebuild over <paramref name="size"/> rows (or displaced ids) runs in parallel.
        /// </summary>
        /// <param name="size">The table or delta size.</param>
        /// <returns><c>true</c> to run in parallel.</returns>
        public bool ParallelizeRebuild(long size) => size > RebuildThreshold && DegreeOfParallelism > 1;

        /// <summary>
        /// Gets whether an operation over <paramref name="containers"/> containers of one memo table runs in parallel.
        /// </summary>
        /// <param name="containers">The number of containers.</param>
        /// <returns><c>true</c> to run in parallel.</returns>
        public bool ParallelizeIntraContainerOp(long containers) => containers > IntraContainerThreshold && DegreeOfParallelism > 1;

        /// <summary>
        /// Gets whether an operation across <paramref name="tables"/> container memo tables runs in parallel.
        /// </summary>
        /// <param name="tables">The number of memo tables.</param>
        /// <returns><c>true</c> to run in parallel.</returns>
        public bool ParallelizeInterContainerOp(long tables) => tables > InterContainerThreshold && DegreeOfParallelism > 1;

    }

}

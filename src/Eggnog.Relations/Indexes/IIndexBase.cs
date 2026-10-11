using System;

namespace Eggnog.Relations
{

    /// <summary>
    /// What <see cref="Index{TIndex}"/> needs of the index it keeps up to date: egglog's <c>IndexBase</c> trait
    /// (core-relations/src/hash_index/mod.rs:146-189), implemented by <see cref="ColumnIndex"/> and
    /// <see cref="TupleIndex"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every member is called once per refresh, never per row: the per-row loops live in the sealed implementations,
    /// where the scan is specialized for their own sinks. Probing is not part of the contract, because the two key
    /// types differ (one value or a tuple); callers probe <see cref="Index{TIndex}.Data"/>, whose concrete type they
    /// know, so the probe is a direct, inlinable call.
    /// </para>
    /// <para>
    /// The building methods add the live rows of a subset of a table, projected onto the index's columns, and require
    /// those rows to be greater than every row already indexed (append-only discipline): egglog's refresh relies on it.
    /// </para>
    /// </remarks>
    /// <typeparam name="TSelf">The implementing type.</typeparam>
    internal interface IIndexBase<TSelf>
        where TSelf : class, IIndexBase<TSelf>
    {

        /// <summary>
        /// Creates an empty index for keys of <paramref name="keyArity"/> columns, sharded as
        /// <paramref name="parallelism"/> says.
        /// </summary>
        /// <param name="keyArity">The number of key columns.</param>
        /// <param name="parallelism">The parallelism settings: shard count and parallel build threshold.</param>
        /// <returns>The index.</returns>
        static abstract TSelf Create(int keyArity, ParallelHeuristics parallelism);

        /// <summary>
        /// Gets the parallelism settings the index was created with.
        /// </summary>
        ParallelHeuristics Parallelism { get; }

        /// <summary>
        /// Gets the number of distinct keys. egglog's <c>len</c>.
        /// </summary>
        int Count { get; }

        /// <summary>
        /// Removes every key, keeping the storage. egglog's <c>clear</c>.
        /// </summary>
        void Clear();

        /// <summary>
        /// Adds the live rows of <paramref name="subset"/> of <paramref name="table"/>, one row at a time: egglog's
        /// <c>refresh_serial</c> loop of <c>scan_project</c> and <c>merge_rows</c>, with the scan writing straight into
        /// the index instead of through a buffer.
        /// </summary>
        /// <param name="table">The table.</param>
        /// <param name="columns">The key columns.</param>
        /// <param name="subset">The rows to add.</param>
        void MergeSubset(Table table, ReadOnlySpan<ColumnId> columns, SubsetRef subset);

        /// <summary>
        /// Builds the index from scratch from the live rows of <paramref name="subset"/>; the index must be empty.
        /// egglog's <c>rebuild_full</c>.
        /// </summary>
        /// <param name="table">The table.</param>
        /// <param name="columns">The key columns.</param>
        /// <param name="subset">The rows to index.</param>
        void RebuildFull(Table table, ReadOnlySpan<ColumnId> columns, SubsetRef subset);

        /// <summary>
        /// Adds the live rows of <paramref name="subset"/> using several threads. egglog's <c>merge_parallel</c>.
        /// </summary>
        /// <param name="table">The table.</param>
        /// <param name="columns">The key columns.</param>
        /// <param name="subset">The rows to add.</param>
        void MergeParallel(Table table, ReadOnlySpan<ColumnId> columns, SubsetRef subset);

        /// <summary>
        /// Creates an independent deep copy. egglog's derived <c>Clone</c>.
        /// </summary>
        /// <returns>The copy.</returns>
        TSelf Clone();

    }

}

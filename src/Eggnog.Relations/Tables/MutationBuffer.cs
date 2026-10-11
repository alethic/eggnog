using System;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace Eggnog.Relations
{

    /// <summary>
    /// A handle for staging writes to one table: egglog's <c>MutationBuffer</c> trait
    /// (core-relations/src/table_spec.rs:364-382) and both of its implementations, the sorted-writes <c>Buffer</c>
    /// (core-relations/src/table/mod.rs:227-287) and the union-find's <c>UfBuffer</c> (core-relations/src/uf/mod.rs:222-258).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Staged rows are pre-sharded by the hash of their key columns into one <see cref="RowBuffer"/> per shard, and
    /// staged removal keys likewise into one <see cref="ArbitraryRowBuffer"/> per shard (it accepts the empty key of a
    /// table without key columns). Nothing is visible to the table until <see cref="Flush"/> publishes the buffers to
    /// its <see cref="PendingState"/> and the table's <see cref="Table.Merge"/> runs.
    /// </para>
    /// <para>
    /// Rust flushes in <c>Drop</c>; C# has no deterministic destructor, so the owner calls <see cref="Flush"/>
    /// explicitly (an <see cref="ExecutionState"/> flushes all of its buffers at once). A buffer that is never flushed
    /// loses its writes. After a flush the buffer is empty and may be reused. egglog's buffer holds a weak reference so
    /// that writes to a dropped table vanish; this one holds a strong reference to the state it was created from, so
    /// flushing after the table was cloned or replaced publishes into the orphaned state, with the same effect.
    /// </para>
    /// <para>
    /// One sealed class serves every table kind: a single-shard table (the union-find) skips hashing, and a table that
    /// does not allow deletion throws on <see cref="StageRemove(ReadOnlySpan{uint})"/> as egglog's <c>UfBuffer</c>
    /// panics. Not thread-safe: each thread stages into its own handle (<see cref="FreshHandle"/>); any number of
    /// handles may flush concurrently.
    /// </para>
    /// </remarks>
    internal sealed class MutationBuffer
    {

        readonly PendingState state;
        readonly int arity;
        readonly int keyCount;
        readonly bool allowsDelete;
        readonly ShardData shardData;
        readonly RowBuffer?[] rows;
        readonly ArbitraryRowBuffer?[] removals;
        bool dirty;

        /// <summary>
        /// Initializes an empty buffer that publishes into <paramref name="state"/>.
        /// </summary>
        /// <param name="state">The table's pending state.</param>
        /// <param name="arity">The table's arity; at least one.</param>
        /// <param name="keyCount">The table's number of key columns.</param>
        /// <param name="allowsDelete">Whether removals may be staged.</param>
        public MutationBuffer(PendingState state, int arity, int keyCount, bool allowsDelete)
        {
            ArgumentNullException.ThrowIfNull(state);
            ArgumentOutOfRangeException.ThrowIfLessThan(arity, 1);
            ArgumentOutOfRangeException.ThrowIfNegative(keyCount);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(keyCount, arity);

            this.state = state;
            this.arity = arity;
            this.keyCount = keyCount;
            this.allowsDelete = allowsDelete;
            shardData = state.ShardData;
            rows = new RowBuffer?[state.ShardCount];
            removals = new ArbitraryRowBuffer?[state.ShardCount];
        }

        /// <summary>
        /// Gets the number of cells per staged row.
        /// </summary>
        public int Arity => arity;

        /// <summary>
        /// Gets the number of key cells per staged removal.
        /// </summary>
        public int KeyCount => keyCount;

        /// <summary>
        /// Gets whether anything was staged since the last <see cref="Flush"/>.
        /// </summary>
        public bool IsEmpty => dirty == false;

        /// <summary>
        /// Stages <paramref name="row"/> for insertion. Amortized O(<see cref="Arity"/>) plus one key hash. egglog's
        /// <c>stage_insert</c>.
        /// </summary>
        /// <param name="row">The row; exactly <see cref="Arity"/> cells, copied.</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void StageInsert(ReadOnlySpan<uint> row)
        {
            if (row.Length != arity)
                ThrowLength(row.Length, arity, nameof(row));

            var shard = shardData.Log2 == 0 ? 0 : shardData.ShardOf(TableKeyHash.Hash(row, keyCount));
            (rows[shard] ??= new RowBuffer(arity)).AddRow(row);
            dirty = true;
        }

        /// <summary>
        /// Stages the row whose cells start at <paramref name="source"/>[<paramref name="offset"/>] for insertion. The
        /// array form for compiled plans.
        /// </summary>
        /// <param name="source">The array holding the row.</param>
        /// <param name="offset">The index of the row's first cell.</param>
        public void StageInsert(uint[] source, int offset)
        {
            ArgumentNullException.ThrowIfNull(source);
            if (offset < 0 || source.Length - offset < arity)
                throw new ArgumentOutOfRangeException(nameof(offset), offset, "The source array does not hold a whole row at the offset.");

            StageInsert(new ReadOnlySpan<uint>(source, offset, arity));
        }

        /// <summary>
        /// Stages the removal of the row with key <paramref name="key"/>, if there is one when the table merges.
        /// Removals of a merge apply before its inserts. egglog's <c>stage_remove</c>.
        /// </summary>
        /// <param name="key">The key; exactly <see cref="KeyCount"/> cells, copied.</param>
        /// <exception cref="InvalidOperationException">The table does not allow deletion.</exception>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void StageRemove(ReadOnlySpan<uint> key)
        {
            if (allowsDelete == false)
                ThrowNoDelete();

            if (key.Length != keyCount)
                ThrowLength(key.Length, keyCount, nameof(key));

            var shard = shardData.Log2 == 0 ? 0 : shardData.ShardOf(TableKeyHash.Hash(key, keyCount));
            (removals[shard] ??= new ArbitraryRowBuffer(keyCount)).AddRow(key);
            dirty = true;
        }

        /// <summary>
        /// Stages the removal of the key whose cells start at <paramref name="source"/>[<paramref name="offset"/>]. The
        /// array form for compiled plans.
        /// </summary>
        /// <param name="source">The array holding the key.</param>
        /// <param name="offset">The index of the key's first cell.</param>
        public void StageRemove(uint[] source, int offset)
        {
            ArgumentNullException.ThrowIfNull(source);
            if (offset < 0 || source.Length - offset < keyCount)
                throw new ArgumentOutOfRangeException(nameof(offset), offset, "The source array does not hold a whole key at the offset.");

            StageRemove(new ReadOnlySpan<uint>(source, offset, keyCount));
        }

        /// <summary>
        /// Creates an empty buffer for the same table. egglog's <c>fresh_handle</c>.
        /// </summary>
        /// <returns>The new buffer.</returns>
        public MutationBuffer FreshHandle()
        {
            return new MutationBuffer(state, arity, keyCount, allowsDelete);
        }

        /// <summary>
        /// Publishes everything staged since the last flush to the table's pending state and empties the buffer: the
        /// work of egglog's <c>Drop for Buffer</c>. One queue operation per nonempty shard. Thread-safe with respect to
        /// other buffers of the same table; the staged writes become visible at the table's next merge.
        /// </summary>
        public void Flush()
        {
            if (dirty == false)
                return;

            for (var i = 0; i < rows.Length; i++)
            {
                var buffer = rows[i];
                if (buffer is not null)
                {
                    rows[i] = null;
                    state.EnqueueRows(i, buffer);
                }

                var keys = removals[i];
                if (keys is not null)
                {
                    removals[i] = null;
                    state.EnqueueRemovals(i, keys);
                }
            }

            dirty = false;
        }

        [DoesNotReturn]
        [MethodImpl(MethodImplOptions.NoInlining)]
        static void ThrowLength(int length, int expected, string name)
        {
            throw new ArgumentException($"Expected {expected} cells but got {length}.", name);
        }

        [DoesNotReturn]
        [MethodImpl(MethodImplOptions.NoInlining)]
        static void ThrowNoDelete()
        {
            throw new InvalidOperationException("This table does not allow deletion (egglog: attempting to remove data from a DisplacedTable).");
        }

    }

}

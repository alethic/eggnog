using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace Eggnog.Relations
{

    internal sealed partial class ContextTables
    {

        /// <summary>
        /// Scratch for keys passed as spans to the write-phase lookups. One per thread.
        /// </summary>
        [ThreadStatic]
        static uint[]? keyScratch;

        /// <summary>
        /// Finds the row of <paramref name="table"/> the context sees with the key at
        /// <paramref name="key"/>[<paramref name="offset"/>], searching the layers from depth <paramref name="top"/> down
        /// (§6.5, the write phase: merges, merge functions, catch-up and predictions). The first layer whose key index
        /// holds the key decides (Lemma F): its row if no level above it hides the row, otherwise none.
        /// </summary>
        /// <remarks>
        /// Reads hidden sets in any state, so it is exact inside the context's merge, where hides are not sealed yet.
        /// Skips the ancestor layers of a context-private table and those below a cleared level. Cost: one hash probe per
        /// layer probed (<see cref="LayerStatistics.LookupProbes"/>), plus O(depth) hidden-set probes on a hit.
        /// </remarks>
        /// <param name="table">A registered function table.</param>
        /// <param name="key">The array holding the key.</param>
        /// <param name="offset">The index of the key's first cell.</param>
        /// <param name="keyHash">The key's hash (<see cref="TableKeyHash"/>), shared by every layer.</param>
        /// <param name="top">The deepest layer to search: <see cref="Depth"/>, or <c>Depth - 1</c> to skip the own layer.</param>
        /// <param name="layer">The table of the layer holding the row.</param>
        /// <param name="depthFound">The depth of that layer.</param>
        /// <param name="row">The row id in that layer.</param>
        /// <returns><c>true</c> if the context sees a row with the key.</returns>
        /// <exception cref="InvalidOperationException">The own layer is taken out of the database by a merge in progress:
        /// a merge function read a table of its own stratum (edge case 16; the root throws
        /// <see cref="KeyNotFoundException"/>).</exception>
        internal bool TryFindVisible(TableId table, uint[] key, int offset, ulong keyHash, int top, [NotNullWhen(true)] out SortedWritesTable? layer, out int depthFound, out int row)
        {
            var s = schema[table] ?? throw new KeyNotFoundException($"Table {table} is not registered with the store.");
            var floor = ClearedFloor(table);
            for (var j = Math.Min(top, depth); j >= floor; j--)
            {
                if (j < depth && s.IsContextPrivate)
                    break;

                var candidate = AncestorLayer(j, table);
                if (candidate is null)
                    continue;

                if (j == depth && owner.Database.IsTakenOut(table))
                    ThrowSameStratum(table);

                var found = candidate.FindRowHashed(key, offset, keyHash);
                statistics.LookupProbes++;
                if (found < 0)
                    continue;

                if (j < depth && HiddenBetween(table, j, found))
                    break;

                layer = candidate;
                depthFound = j;
                row = found;
                return true;
            }

            layer = null;
            depthFound = -1;
            row = -1;
            return false;
        }

        /// <summary>
        /// Copies the row of <paramref name="table"/> the context sees with key <paramref name="key"/> into
        /// <paramref name="destination"/>: the write-phase point lookup (<see cref="ExecutionState.PredictRow(TableId, ReadOnlySpan{uint}, ReadOnlySpan{MergeVal}, Span{uint})"/>
        /// in a child). The key is copied to a per-thread array and hashed once.
        /// </summary>
        /// <param name="table">A registered function table.</param>
        /// <param name="key">The key.</param>
        /// <param name="destination">Receives the row; at least as many cells as the table has columns.</param>
        /// <returns><c>true</c> if the context sees a row with the key.</returns>
        public bool TryGetVisibleRow(TableId table, ReadOnlySpan<uint> key, Span<uint> destination)
        {
            var s = schema[table] ?? throw new KeyNotFoundException($"Table {table} is not registered with the store.");
            if (destination.Length < s.Arity)
                throw new ArgumentException($"The destination needs room for {s.Arity} cells.", nameof(destination));

            var buffer = PrepareKey(s, key, out var keyHash);
            if (TryFindVisible(table, buffer, 0, keyHash, depth, out var layer, out _, out var row) == false)
                return false;

            layer.Rows.GetRow(row).CopyTo(destination);
            return true;
        }

        /// <summary>
        /// Gets column <paramref name="column"/> of the row of <paramref name="table"/> the context sees with key
        /// <paramref name="key"/>: the write-phase column lookup (<see cref="ExecutionState.PredictColumn(TableId, ReadOnlySpan{uint}, ReadOnlySpan{MergeVal}, ColumnId)"/>
        /// in a child).
        /// </summary>
        /// <param name="table">A registered function table.</param>
        /// <param name="key">The key.</param>
        /// <param name="column">The column.</param>
        /// <param name="value">The value, or zero.</param>
        /// <returns><c>true</c> if the context sees a row with the key.</returns>
        public bool TryGetVisibleColumn(TableId table, ReadOnlySpan<uint> key, ColumnId column, out uint value)
        {
            var s = schema[table] ?? throw new KeyNotFoundException($"Table {table} is not registered with the store.");
            if ((uint)column.Rep >= (uint)s.Arity)
                throw new ArgumentOutOfRangeException(nameof(column), column, $"The table has {s.Arity} columns.");

            var buffer = PrepareKey(s, key, out var keyHash);
            if (TryFindVisible(table, buffer, 0, keyHash, depth, out var layer, out _, out var row) == false)
            {
                value = 0;
                return false;
            }

            value = layer.Rows.Data[row * layer.Arity + column.Rep];
            return true;
        }

        /// <summary>
        /// Copies a span key into this thread's scratch array and hashes it.
        /// </summary>
        static uint[] PrepareKey(LayeredTableSchema s, ReadOnlySpan<uint> key, out ulong keyHash)
        {
            if (key.Length != s.KeyCount)
                throw new ArgumentException($"Expected a key of {s.KeyCount} cells but got {key.Length}.", nameof(key));

            var buffer = keyScratch;
            if (buffer is null || buffer.Length < key.Length)
                keyScratch = buffer = new uint[Math.Max(key.Length, 8)];

            key.CopyTo(buffer);
            keyHash = TableKeyHash.Hash(buffer, 0, key.Length);
            return buffer;
        }

        [DoesNotReturn]
        [MethodImpl(MethodImplOptions.NoInlining)]
        static void ThrowSameStratum(TableId table)
        {
            throw new InvalidOperationException($"Table {table} is being merged in this context; a merge function cannot read a table of its own stratum (declare it a read dependency instead).");
        }

    }

}

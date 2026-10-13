using System;
using System.Runtime.CompilerServices;

namespace Eggnog.Relations
{

    /// <summary>
    /// An <see cref="IRowSink"/> that copies each scanned key and its row id into flat arrays: one batch of a
    /// <see cref="TupleIndex"/>'s parallel build (egglog's per-batch <c>TaggedRowBuffer</c>,
    /// core-relations/src/hash_index/mod.rs:674-686). Fed by
    /// <see cref="Table.ScanProject{TSink}(SubsetRef, ReadOnlySpan{ColumnId}, int, int, ReadOnlySpan{Constraint}, ref TSink)"/>.
    /// </summary>
    /// <remarks>
    /// The arrays must hold the batch: as many rows as the batch has positions. A struct passed by reference; read
    /// <see cref="Count"/> after the scan.
    /// </remarks>
    internal struct TupleBatchSink : IRowSink
    {

        readonly int[] rows;
        readonly uint[] keys;
        readonly int arity;
        int count;

        /// <summary>
        /// Initializes a sink writing from position zero.
        /// </summary>
        /// <param name="rows">Receives the row ids.</param>
        /// <param name="keys">Receives the keys, <paramref name="arity"/> cells each.</param>
        /// <param name="arity">The key arity.</param>
        public TupleBatchSink(int[] rows, uint[] keys, int arity)
        {
            this.rows = rows;
            this.keys = keys;
            this.arity = arity;
            count = 0;
        }

        /// <summary>
        /// Gets the number of keys written.
        /// </summary>
        public readonly int Count => count;

        /// <inheritdoc />
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void AddRow(int rowId, ReadOnlySpan<uint> row)
        {
            row.CopyTo(keys.AsSpan(count * arity, arity));
            rows[count++] = rowId;
        }

    }

}

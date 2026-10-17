using System;
using System.Runtime.CompilerServices;

namespace Eggnog.Relations
{

    /// <summary>
    /// A row buffer that also accepts arity zero: egglog's <c>ArbitraryRowBuffer</c>
    /// (core-relations/src/table/mod.rs:174-225). Tables stage removals by key in one per shard, and a table with no
    /// key columns removes "rows of nothing"; a <see cref="RowBuffer"/> cannot hold those, so arity zero is just a row
    /// counter.
    /// </summary>
    /// <remarks>
    /// As egglog notes, code truly optimized for arity zero would look different; this keeps the deletion path
    /// <see cref="RowBuffer"/>-centric. Not thread-safe.
    /// </remarks>
    internal sealed class ArbitraryRowBuffer : IDisposable
    {

        readonly int arity;
        readonly RowBuffer? buffer;
        int emptyRows;

        /// <summary>
        /// Initializes an empty buffer for rows of <paramref name="arity"/> cells.
        /// </summary>
        /// <param name="arity">The number of cells per row; may be zero.</param>
        public ArbitraryRowBuffer(int arity)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(arity);

            this.arity = arity;
            buffer = arity == 0 ? null : new RowBuffer(arity);
        }

        ArbitraryRowBuffer(int arity, RowBuffer? buffer, int emptyRows)
        {
            this.arity = arity;
            this.buffer = buffer;
            this.emptyRows = emptyRows;
        }

        /// <summary>
        /// Gets the number of cells per row.
        /// </summary>
        public int Arity => arity;

        /// <summary>
        /// Gets the number of rows added. egglog's <c>len</c>.
        /// </summary>
        public int Count
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => buffer is null ? emptyRows : buffer.Count;
        }

        /// <summary>
        /// Gets whether no rows have been added.
        /// </summary>
        public bool IsEmpty => Count == 0;

        /// <summary>
        /// Gets the underlying row buffer, or <c>null</c> for arity zero. For raw access to the rows.
        /// </summary>
        public RowBuffer? Buffer => buffer;

        /// <summary>
        /// Appends a row. Amortized O(<see cref="Arity"/>); O(1) for arity zero.
        /// </summary>
        /// <param name="row">The row's cells; exactly <see cref="Arity"/> of them.</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void AddRow(ReadOnlySpan<uint> row)
        {
            if (buffer is null)
            {
                if (row.Length != 0)
                    throw new ArgumentException($"Expected an empty row but got {row.Length} cells.", nameof(row));

                emptyRows++;
            }
            else
            {
                buffer.AddRow(row);
            }
        }

        /// <summary>
        /// Enumerates the rows in insertion order; for arity zero, <see cref="Count"/> empty rows. egglog's
        /// <c>for_each</c>.
        /// </summary>
        /// <returns>The enumerator.</returns>
        public RowBufferEnumerator EnumerateRows()
        {
            return buffer is null ? new RowBufferEnumerator(Array.Empty<uint>(), 0, emptyRows) : buffer.EnumerateRows();
        }

        /// <summary>
        /// Copies the buffer. egglog derives <c>Clone</c>.
        /// </summary>
        /// <returns>The copy, which the caller owns.</returns>
        public ArbitraryRowBuffer Clone()
        {
            return new ArbitraryRowBuffer(arity, buffer?.Clone(), emptyRows);
        }

        /// <summary>
        /// Returns the storage to the shared array pool and empties the buffer. Disposing twice is harmless.
        /// </summary>
        public void Dispose()
        {
            buffer?.Dispose();
            emptyRows = 0;
        }

    }

}

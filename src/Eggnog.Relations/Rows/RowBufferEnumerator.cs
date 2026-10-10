using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Eggnog.Relations
{

    /// <summary>
    /// Enumerates every row of a row-major cell array in id order, stale rows included: egglog's
    /// <c>RowBuffer::iter</c>. Returned by <see cref="RowBuffer.EnumerateRows"/> and
    /// <see cref="ArbitraryRowBuffer.EnumerateRows"/>; use it with <c>foreach</c>, or call <see cref="MoveNext"/>
    /// yourself to read <see cref="Row"/> as well.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Works for arity zero too, yielding <c>count</c> empty rows (egglog's <c>ArbitraryRowBuffer::for_each</c>).
    /// </para>
    /// <para>
    /// The enumerator captures the storage array and row count when created. Rows added afterwards are not visited.
    /// Growing the buffer while enumerating leaves the enumerator on the old storage, which growth drops to the
    /// garbage collector rather than recycling: it keeps reading the rows as they were, and writes through
    /// <see cref="Current"/> no longer reach the buffer. Disposing the buffer, or handing its storage to a parallel
    /// writer, while enumerating is not allowed: the storage then belongs to someone else.
    /// </para>
    /// <para>
    /// Cost: the rows are range-checked once, when the enumerator is created; <see cref="MoveNext"/> and
    /// <see cref="Current"/> do no bounds checks, like egglog's <c>chunks</c>.
    /// </para>
    /// </remarks>
    public ref struct RowBufferEnumerator
    {

        readonly uint[] data;
        readonly int arity;
        readonly int count;
        int row;
        Span<uint> current;

        /// <summary>
        /// Initializes an enumerator over the first <paramref name="count"/> rows of <paramref name="data"/>.
        /// </summary>
        /// <param name="data">The storage.</param>
        /// <param name="arity">The cells per row; may be zero.</param>
        /// <param name="count">The number of rows.</param>
        internal RowBufferEnumerator(uint[] data, int arity, int count)
        {
            Debug.Assert(arity >= 0 && count >= 0);

            // The one range check that lets MoveNext address rows without bounds checks.
            if ((long)arity * count > data.Length)
                throw new ArgumentOutOfRangeException(nameof(count), count, "The rows do not fit in the storage.");

            this.data = data;
            this.arity = arity;
            this.count = count;
            row = -1;
            current = default;
        }

        /// <summary>
        /// Gets the id of the current row.
        /// </summary>
        public readonly int Row => row;

        /// <summary>
        /// Gets the current row's cells. Writable, for in-place updates (egglog's <c>non_stale_mut</c> pattern). Empty
        /// before the first <see cref="MoveNext"/> and after the last.
        /// </summary>
        public readonly Span<uint> Current
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => current;
        }

        /// <summary>
        /// Returns this enumerator, so that it works with <c>foreach</c>.
        /// </summary>
        /// <returns>This enumerator.</returns>
        public readonly RowBufferEnumerator GetEnumerator() => this;

        /// <summary>
        /// Advances to the next row.
        /// </summary>
        /// <returns><c>false</c> after the last row.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool MoveNext()
        {
            // Compared before incrementing, so that an arity-zero enumerator of int.MaxValue rows cannot overflow.
            if (row < count - 1)
            {
                // r < count and arity * count <= data.Length (checked at construction), so the row lies in data.
                var r = ++row;
                current = MemoryMarshal.CreateSpan(ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(data), r * arity), arity);
                return true;
            }

            row = count;
            current = default;
            return false;
        }

    }

}

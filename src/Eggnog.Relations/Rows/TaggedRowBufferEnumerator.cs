using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Eggnog.Relations
{

    /// <summary>
    /// Enumerates every row of a <see cref="TaggedRowBuffer"/> with its source row id, stale rows included, in
    /// insertion order: egglog's <c>TaggedRowBuffer::iter</c>. Use it with <c>foreach</c>, or call
    /// <see cref="MoveNext"/> yourself to read <see cref="Row"/> (the row's id in the buffer) as well.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Captures the storage array and row count when created. Rows added afterwards are not visited. After a growth
    /// it stays on the old storage, which growth drops to the garbage collector rather than recycling, so it reads the
    /// rows as they were and writes through <see cref="TaggedRow.Values"/> no longer reach the buffer. The buffer must
    /// not be disposed while it runs.
    /// </para>
    /// <para>
    /// Cost: the rows are range-checked once, when the enumerator is created; <see cref="MoveNext"/> reads each row's
    /// tag and builds its span with no bounds checks.
    /// </para>
    /// </remarks>
    internal ref struct TaggedRowBufferEnumerator
    {

        readonly uint[] data;
        readonly int arity;
        readonly int count;
        int row;
        int rowId;
        Span<uint> values;

        /// <summary>
        /// Initializes an enumerator over the first <paramref name="count"/> tagged rows of <paramref name="data"/>.
        /// </summary>
        /// <param name="data">The storage.</param>
        /// <param name="arity">The cells per row, not counting the tag.</param>
        /// <param name="count">The number of rows.</param>
        internal TaggedRowBufferEnumerator(uint[] data, int arity, int count)
        {
            Debug.Assert(arity >= 0 && count >= 0);

            // The one range check that lets MoveNext address rows and tags without bounds checks.
            if (((long)arity + 1) * count > data.Length)
                throw new ArgumentOutOfRangeException(nameof(count), count, "The rows do not fit in the storage.");

            this.data = data;
            this.arity = arity;
            this.count = count;
            row = -1;
            rowId = 0;
            values = default;
        }

        /// <summary>
        /// Gets the current row's id in the buffer (not its source row id).
        /// </summary>
        public readonly int Row => row;

        /// <summary>
        /// Gets the current row: its source row id and its cells. Empty, with source id zero, before the first
        /// <see cref="MoveNext"/> and after the last.
        /// </summary>
        public readonly TaggedRow Current
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => new(rowId, values);
        }

        /// <summary>
        /// Returns this enumerator, so that it works with <c>foreach</c>.
        /// </summary>
        /// <returns>This enumerator.</returns>
        public readonly TaggedRowBufferEnumerator GetEnumerator() => this;

        /// <summary>
        /// Advances to the next row.
        /// </summary>
        /// <returns><c>false</c> after the last row.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool MoveNext()
        {
            if (row < count - 1)
            {
                // r < count and (arity + 1) * count <= data.Length (checked at construction), so the row and its tag
                // lie in data.
                var r = ++row;
                ref var first = ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(data), r * (arity + 1));
                rowId = (int)Unsafe.Add(ref first, arity);
                values = MemoryMarshal.CreateSpan(ref first, arity);
                return true;
            }

            row = count;
            rowId = 0;
            values = default;
            return false;
        }

    }

}

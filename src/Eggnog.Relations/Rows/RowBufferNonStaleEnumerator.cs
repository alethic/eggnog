using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Eggnog.Relations
{

    /// <summary>
    /// Enumerates the rows of a <see cref="RowBuffer"/> whose first cell is not <see cref="Value.StaleRep"/>, in id
    /// order: egglog's <c>non_stale</c> and <c>non_stale_mut</c>. Use it with <c>foreach</c>, or call
    /// <see cref="MoveNext"/> yourself to read <see cref="Row"/> as well.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Captures the storage array and row count when created, with the same rules as
    /// <see cref="RowBufferEnumerator"/>: after a growth it stays on the old storage (dropped to the garbage collector,
    /// never recycled), and the buffer must not be disposed while it runs. Marking the current row stale while
    /// enumerating is fine.
    /// </para>
    /// <para>
    /// Cost: the rows are range-checked once, when the enumerator is created; <see cref="MoveNext"/> probes rows and
    /// <see cref="Current"/> returns them with no bounds checks.
    /// </para>
    /// </remarks>
    public ref struct RowBufferNonStaleEnumerator
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
        /// <param name="arity">The cells per row; at least one.</param>
        /// <param name="count">The number of rows.</param>
        internal RowBufferNonStaleEnumerator(uint[] data, int arity, int count)
        {
            Debug.Assert(arity >= 1 && count >= 0);

            // The one range check that lets MoveNext probe rows without bounds checks.
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
        /// Gets the current row's cells. Writable, for in-place updates. Empty before the first
        /// <see cref="MoveNext"/> and after the last.
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
        public readonly RowBufferNonStaleEnumerator GetEnumerator() => this;

        /// <summary>
        /// Advances to the next row that is not stale.
        /// </summary>
        /// <returns><c>false</c> when no such row remains.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool MoveNext()
        {
            var r = row;
            if (r >= count)
                return false;

            // Every row below `count` lies inside `data` (checked at construction), so the probe and the span skip
            // bounds checks.
            ref var cells = ref MemoryMarshal.GetArrayDataReference(data);
            do
                r++;
            while (r < count && Unsafe.Add(ref cells, r * arity) == Value.StaleRep);

            row = r;
            if (r < count)
            {
                current = MemoryMarshal.CreateSpan(ref Unsafe.Add(ref cells, r * arity), arity);
                return true;
            }

            current = default;
            return false;
        }

    }

}

using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Eggnog.Relations
{

    /// <summary>
    /// Enumerates the rows of a <see cref="TaggedRowBuffer"/> that are not stale, with their source row ids, in
    /// insertion order: egglog's <c>TaggedRowBuffer::non_stale</c> and <c>non_stale_mut</c>. Use it with
    /// <c>foreach</c>, or call <see cref="MoveNext"/> yourself to read <see cref="Row"/> as well.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Captures the storage array and row count when created, with the same rules as
    /// <see cref="TaggedRowBufferEnumerator"/>: after a growth it stays on the old storage (dropped to the garbage
    /// collector, never recycled), and the buffer must not be disposed while it runs. Marking the current row stale
    /// while enumerating is fine.
    /// </para>
    /// <para>
    /// Cost: the rows are range-checked once, when the enumerator is created; <see cref="MoveNext"/> probes rows,
    /// reads tags and builds spans with no bounds checks.
    /// </para>
    /// </remarks>
    public ref struct TaggedRowBufferNonStaleEnumerator
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
        internal TaggedRowBufferNonStaleEnumerator(uint[] data, int arity, int count)
        {
            Debug.Assert(arity >= 0 && count >= 0);

            // The one range check that lets MoveNext probe rows without bounds checks.
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
        public readonly TaggedRowBufferNonStaleEnumerator GetEnumerator() => this;

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

            // Every row below `count`, tag included, lies inside `data` (checked at construction), so the probe, the
            // tag read and the span skip bounds checks.
            var stride = arity + 1;
            ref var cells = ref MemoryMarshal.GetArrayDataReference(data);
            do
                r++;
            while (r < count && Unsafe.Add(ref cells, r * stride) == Value.StaleRep);

            row = r;
            if (r < count)
            {
                ref var first = ref Unsafe.Add(ref cells, r * stride);
                rowId = (int)Unsafe.Add(ref first, arity);
                values = MemoryMarshal.CreateSpan(ref first, arity);
                return true;
            }

            rowId = 0;
            values = default;
            return false;
        }

    }

}

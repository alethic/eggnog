using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace Eggnog.Relations
{

    /// <summary>
    /// A half-open range <c>[Start, End)</c> of row ids: the dense form of a set of rows. Port of egglog's
    /// <c>OffsetRange</c> (core-relations/src/offsets/mod.rs:28-55).
    /// </summary>
    /// <remarks>
    /// Invariant: <c>0 &lt;= Start &lt;= End</c>, checked with <see cref="Debug.Assert(bool)"/> as egglog
    /// <c>debug_assert</c>s it. A range with <c>Start == End</c> is empty whatever its position, so test emptiness
    /// with <see cref="IsEmpty"/>, not by comparing against <c>default</c>. Copying is free: two
    /// <see cref="int"/>s.
    /// </remarks>
    public readonly record struct OffsetRange
    {

        /// <summary>
        /// Initializes a new instance covering <c>[<paramref name="start"/>, <paramref name="end"/>)</c>.
        /// </summary>
        /// <param name="start">The first row id in the range.</param>
        /// <param name="end">One past the last row id in the range.</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public OffsetRange(int start, int end)
        {
            Debug.Assert(0 <= start && start <= end, $"attempting to create malformed range {start}..{end}");
            Start = start;
            End = end;
        }

        /// <summary>
        /// Gets the first row id in the range.
        /// </summary>
        public int Start { get; }

        /// <summary>
        /// Gets one past the last row id in the range.
        /// </summary>
        public int End { get; }

        /// <summary>
        /// Gets the number of row ids in the range. O(1).
        /// </summary>
        public int Size
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => End - Start;
        }

        /// <summary>
        /// Gets whether the range holds no row id. O(1).
        /// </summary>
        public bool IsEmpty
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => End == Start;
        }

        /// <summary>
        /// Calls <paramref name="visitor"/> on every row id in ascending order. Port of <c>Offsets::offsets</c> for
        /// <c>OffsetRange</c>. O(<see cref="Size"/>), no allocation.
        /// </summary>
        /// <typeparam name="TVisitor">The visitor type; a struct, so the loop is specialized per visitor.</typeparam>
        /// <param name="visitor">The visitor.</param>
        internal void ForEach<TVisitor>(ref TVisitor visitor)
            where TVisitor : struct, IOffsetVisitor
        {
            var end = End;
            for (var row = Start; row < end; row++)
                visitor.Visit(row);
        }

        /// <summary>
        /// Gets an enumerator over the row ids in ascending order.
        /// </summary>
        /// <returns>The enumerator.</returns>
        public OffsetEnumerator GetEnumerator() => new(null, Start, End);

        /// <inheritdoc />
        public override string ToString() => $"[{Start}..{End})";

    }

}

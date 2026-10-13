using System.Runtime.CompilerServices;

namespace Eggnog.Relations
{

    /// <summary>
    /// Answers "is this row hidden?" for the two sealed sets of a <see cref="HiddenRows"/> while a scan walks a layer's
    /// rows in ascending order (colored-contexts spec §6.3): a merge walk with galloping over a sorted set, or one bit test
    /// over a bitset. A mutable struct; keep it in a local or a field of the scanning struct and pass it by reference.
    /// </summary>
    /// <remarks>
    /// Rows asked in nondecreasing order cost O(log gap) each, amortized O(1) over a dense scan. A row asked out of order
    /// restarts the walk from the beginning, so the answer is always exact. The sets must stay sealed while the cursor
    /// is in use, which they do outside a merge of their owner (I18).
    /// </remarks>
    internal struct HiddenCursor
    {

        readonly int[]? frozenRows;
        readonly ulong[]? frozenBits;
        readonly int[]? liveRows;
        readonly ulong[]? liveBits;
        int frozenPosition;
        int livePosition;
        int last;

        /// <summary>
        /// Initializes a cursor over <paramref name="frozen"/> and <paramref name="live"/>, either of which may be
        /// <c>null</c>.
        /// </summary>
        /// <param name="frozen">The frozen part of the hidden rows.</param>
        /// <param name="live">The live part.</param>
        internal HiddenCursor(HiddenSet? frozen, HiddenSet? live)
        {
            frozenRows = frozen?.SortedRows;
            frozenBits = frozen?.Bits;
            liveRows = live?.SortedRows;
            liveBits = live?.Bits;
            frozenPosition = 0;
            livePosition = 0;
            last = int.MinValue;
        }

        /// <summary>
        /// Gets whether the cursor hides nothing.
        /// </summary>
        public readonly bool IsNone => frozenRows is null && frozenBits is null && liveRows is null && liveBits is null;

        /// <summary>
        /// Gets whether <paramref name="row"/> is hidden.
        /// </summary>
        /// <param name="row">A row id; ascending across calls for the amortized cost.</param>
        /// <returns><c>true</c> if hidden.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool IsHidden(int row)
        {
            if (row < last)
            {
                frozenPosition = 0;
                livePosition = 0;
            }

            last = row;
            return Probe(frozenRows, frozenBits, ref frozenPosition, row) || Probe(liveRows, liveBits, ref livePosition, row);
        }

        /// <summary>
        /// Probes one set from <paramref name="position"/>, the first sorted row not known to be below the rows asked so
        /// far, and advances it.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static bool Probe(int[]? rows, ulong[]? bits, ref int position, int row)
        {
            if (bits is not null)
                return (uint)(row >> 6) < (uint)bits.Length && row >= 0 && (bits[row >> 6] & (1UL << row)) != 0;

            if (rows is null || position >= rows.Length)
                return false;

            var at = rows[position];
            if (at >= row)
                return at == row;

            return Gallop(rows, ref position, row);
        }

        /// <summary>
        /// Finds the first sorted row at or above <paramref name="row"/> after <paramref name="position"/>, whose row is
        /// below it: doubling steps, then a binary search within the last step.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        static bool Gallop(int[] rows, ref int position, int row)
        {
            var n = rows.Length;
            var low = position;
            var step = 1;
            var high = low + step;
            while (high < n && rows[high] < row)
            {
                low = high;
                step <<= 1;
                high = low + step;
            }

            // rows[low] < row, and rows[high] >= row when high < n: the answer lies in (low, min(high, n - 1)].
            var first = low + 1;
            var end = high < n ? high + 1 : n;
            while (first < end)
            {
                var mid = (int)((uint)(first + end) >> 1);
                if (rows[mid] < row)
                    first = mid + 1;
                else
                    end = mid;
            }

            position = first;
            return first < n && rows[first] == row;
        }

    }

}

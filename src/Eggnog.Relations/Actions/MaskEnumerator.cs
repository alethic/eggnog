using System.Numerics;
using System.Runtime.CompilerServices;

namespace Eggnog.Relations
{

    /// <summary>
    /// Enumerates the live lanes of a <see cref="Mask"/> in ascending order, from a snapshot of its words: egglog's
    /// <c>Mask::empty_iter</c> (core-relations/src/action/mask.rs:66-71, 306-333), visiting only live lanes. A struct
    /// enumerator, so <c>foreach</c> over a mask allocates nothing; changing the mask meanwhile does not affect it.
    /// </summary>
    internal struct MaskEnumerator
    {

        ulong low;
        ulong high;
        int current;

        /// <summary>
        /// Initializes an enumerator over the lanes set in <paramref name="low"/> (lanes 0 to 63) and
        /// <paramref name="high"/> (lanes 64 to 127).
        /// </summary>
        /// <param name="low">The low word.</param>
        /// <param name="high">The high word.</param>
        public MaskEnumerator(ulong low, ulong high)
        {
            this.low = low;
            this.high = high;
            current = -1;
        }

        /// <summary>
        /// Gets the current lane.
        /// </summary>
        public readonly int Current => current;

        /// <summary>
        /// Advances to the next live lane.
        /// </summary>
        /// <returns><c>false</c> when no lane is left.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool MoveNext()
        {
            if (low != 0)
            {
                current = BitOperations.TrailingZeroCount(low);
                low &= low - 1;
                return true;
            }

            if (high != 0)
            {
                current = 64 + BitOperations.TrailingZeroCount(high);
                high &= high - 1;
                return true;
            }

            return false;
        }

    }

}

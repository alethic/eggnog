using System;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace Eggnog.Relations
{

    /// <summary>
    /// A fixed-size set of small non-negative integers, stored one bit each.
    /// </summary>
    internal sealed class FixedBitSet
    {

        readonly ulong[] words;

        /// <summary>
        /// Initializes a new instance able to hold <c>0</c> to <paramref name="length"/> - 1.
        /// </summary>
        /// <param name="length">The number of bits.</param>
        public FixedBitSet(int length)
        {
            Length = length;
            words = new ulong[(length + 63) >> 6];
        }

        /// <summary>
        /// Gets the number of bits.
        /// </summary>
        public int Length { get; }

        /// <summary>
        /// Gets whether <paramref name="bit"/> is set. Bits out of range are not set.
        /// </summary>
        /// <param name="bit">The bit.</param>
        /// <returns><c>true</c> if set.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool Contains(int bit) => (uint)bit < (uint)Length && (words[bit >> 6] & (1UL << (bit & 63))) != 0;

        /// <summary>
        /// Sets <paramref name="bit"/>.
        /// </summary>
        /// <param name="bit">The bit.</param>
        /// <returns><c>true</c> if it was not set before.</returns>
        public bool Insert(int bit)
        {
            if ((uint)bit >= (uint)Length)
                throw new ArgumentOutOfRangeException(nameof(bit), $"Bit {bit} exceeds a set of {Length} bits.");

            ref var word = ref words[bit >> 6];
            var mask = 1UL << (bit & 63);
            var was = (word & mask) != 0;
            word |= mask;
            return was == false;
        }

        /// <summary>
        /// Clears <paramref name="bit"/>.
        /// </summary>
        /// <param name="bit">The bit.</param>
        public void Remove(int bit)
        {
            if ((uint)bit < (uint)Length)
                words[bit >> 6] &= ~(1UL << (bit & 63));
        }

        /// <summary>
        /// Gets the number of set bits.
        /// </summary>
        public int Count
        {
            get
            {
                var n = 0;
                foreach (var w in words)
                    n += BitOperations.PopCount(w);

                return n;
            }
        }

        /// <summary>
        /// Adds every bit of <paramref name="other"/>, which must have the same length.
        /// </summary>
        /// <param name="other">The other set.</param>
        public void UnionWith(FixedBitSet other)
        {
            for (var i = 0; i < words.Length; i++)
                words[i] |= other.words[i];
        }

        /// <summary>
        /// Removes every bit of <paramref name="other"/>, which must have the same length.
        /// </summary>
        /// <param name="other">The other set.</param>
        public void ExceptWith(FixedBitSet other)
        {
            for (var i = 0; i < words.Length; i++)
                words[i] &= ~other.words[i];
        }

        /// <summary>
        /// Gets whether every bit of this set is in <paramref name="other"/>.
        /// </summary>
        /// <param name="other">The other set.</param>
        /// <returns><c>true</c> if this is a subset.</returns>
        public bool IsSubsetOf(FixedBitSet other)
        {
            for (var i = 0; i < words.Length; i++)
                if ((words[i] & ~other.words[i]) != 0)
                    return false;

            return true;
        }

        /// <summary>
        /// Clears every bit.
        /// </summary>
        public void Clear() => Array.Clear(words);

        /// <summary>
        /// Calls <paramref name="action"/> for every set bit in ascending order.
        /// </summary>
        /// <param name="action">Receives each set bit.</param>
        public void ForEach(Action<int> action)
        {
            for (var i = 0; i < words.Length; i++)
            {
                var w = words[i];
                while (w != 0)
                {
                    var b = BitOperations.TrailingZeroCount(w);
                    action((i << 6) + b);
                    w &= w - 1;
                }
            }
        }

    }

}

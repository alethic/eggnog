using System.Numerics;
using System.Runtime.CompilerServices;

namespace Eggnog.Relations
{

    /// <summary>
    /// The Fx hash (rustc-hash 2.x), which egglog uses for every key hash: one add and one multiply per word, and a
    /// rotate at the end.
    /// </summary>
    /// <remarks>
    /// It is fast, not collision-resistant. The final rotate spreads entropy into both the high bits (used to pick a
    /// shard) and the low bits (used to pick a slot), which is what the sharded tables rely on.
    /// </remarks>
    internal struct FxHasher
    {

        /// <summary>
        /// The multiplier, from rustc-hash 2.x.
        /// </summary>
        public const ulong Seed = 0xf1357aea2e62a9c5UL;

        ulong hash;

        /// <summary>
        /// Mixes one word into the hash.
        /// </summary>
        /// <param name="word">The word.</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Write(ulong word) => hash = (hash + word) * Seed;

        /// <summary>
        /// Finishes the hash.
        /// </summary>
        /// <returns>The 64-bit hash.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public readonly ulong Finish() => BitOperations.RotateLeft(hash, 26);

        /// <summary>
        /// Hashes one value.
        /// </summary>
        /// <param name="a">The value.</param>
        /// <returns>The hash.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ulong Hash(uint a) => BitOperations.RotateLeft(a * Seed, 26);

        /// <summary>
        /// Hashes two values.
        /// </summary>
        /// <param name="a">The first value.</param>
        /// <param name="b">The second value.</param>
        /// <returns>The hash.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ulong Hash(uint a, uint b) => BitOperations.RotateLeft((a * Seed + b) * Seed, 26);

        /// <summary>
        /// Hashes three values.
        /// </summary>
        /// <param name="a">The first value.</param>
        /// <param name="b">The second value.</param>
        /// <param name="c">The third value.</param>
        /// <returns>The hash.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ulong Hash(uint a, uint b, uint c) => BitOperations.RotateLeft(((a * Seed + b) * Seed + c) * Seed, 26);

        /// <summary>
        /// Hashes a sequence of values.
        /// </summary>
        /// <param name="values">The values.</param>
        /// <returns>The hash.</returns>
        public static ulong Hash(System.ReadOnlySpan<uint> values)
        {
            ulong h = 0;
            foreach (var v in values)
                h = (h + v) * Seed;

            return BitOperations.RotateLeft(h, 26);
        }

    }

}

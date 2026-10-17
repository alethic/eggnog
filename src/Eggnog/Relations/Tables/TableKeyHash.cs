using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Eggnog.Relations
{

    /// <summary>
    /// The hash of a row's key columns: egglog's <c>hash_code</c> (core-relations/src/table/mod.rs:1277-1286), FxHash
    /// over each key cell written as a word. Staging, deleting, inserting and compaction all use it, so a key always
    /// lands in the same shard (<see cref="ShardData.ShardOf"/> of the hash).
    /// </summary>
    /// <remarks>
    /// Keys of one, two and three cells use <see cref="FxHasher"/>'s unrolled forms; a table without key columns hashes
    /// every row to the hasher's initial state, zero, so all its rows share one key.
    /// </remarks>
    internal static class TableKeyHash
    {

        /// <summary>
        /// Hashes the first <paramref name="keyCount"/> cells of <paramref name="row"/>. O(<paramref name="keyCount"/>).
        /// </summary>
        /// <param name="row">The row, or just its key.</param>
        /// <param name="keyCount">The number of key columns.</param>
        /// <returns>The 64-bit hash.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ulong Hash(ReadOnlySpan<uint> row, int keyCount)
        {
            if (row.Length < keyCount)
                ThrowShortRow(row.Length, keyCount);

            return HashUnchecked(ref MemoryMarshal.GetReference(row), keyCount);
        }

        /// <summary>
        /// Hashes the <paramref name="keyCount"/> cells starting at <paramref name="data"/>[<paramref name="offset"/>].
        /// The array form, for keys held in an array: row storage read in place, or a key gathered or copied into a
        /// scratch array. O(<paramref name="keyCount"/>).
        /// </summary>
        /// <param name="data">The array holding the key.</param>
        /// <param name="offset">The index of the key's first cell.</param>
        /// <param name="keyCount">The number of key columns.</param>
        /// <returns>The 64-bit hash.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ulong Hash(uint[] data, int offset, int keyCount)
        {
            if ((uint)offset > (uint)data.Length || data.Length - offset < keyCount)
                ThrowShortRow(data.Length - offset, keyCount);

            return HashUnchecked(ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(data), offset), keyCount);
        }

        /// <summary>
        /// Hashes <paramref name="keyCount"/> cells starting at <paramref name="key"/>, which the caller has checked.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static ulong HashUnchecked(ref uint key, int keyCount)
        {
            switch (keyCount)
            {
                case 0:
                    return 0;
                case 1:
                    return FxHasher.Hash(key);
                case 2:
                    return FxHasher.Hash(key, Unsafe.Add(ref key, 1));
                case 3:
                    return FxHasher.Hash(key, Unsafe.Add(ref key, 1), Unsafe.Add(ref key, 2));
                default:
                    return FxHasher.Hash(MemoryMarshal.CreateReadOnlySpan(ref key, keyCount));
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        static void ThrowShortRow(int length, int keyCount)
        {
            throw new ArgumentException($"A key of {keyCount} cells was expected but only {length} are available.");
        }

    }

}

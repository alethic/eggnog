using System;
using System.Buffers;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Eggnog.Relations
{

    /// <summary>
    /// Allocation, growth and release of the engine's flat arrays (row buffer storage and similar), following egglog's
    /// rules for pooled vectors: an array of more than <see cref="MaxUnpooledLength"/> elements and at most
    /// <see cref="MaxPooledBytes"/> bytes comes from and goes back to <see cref="ArrayPool{T}.Shared"/>; any other
    /// array is an ordinary allocation that is simply dropped.
    /// </summary>
    /// <remarks>
    /// <para>
    /// egglog keeps <c>Vec</c>s in its own thread-local pools, only when their capacity exceeds 256 elements
    /// (core-relations/src/pool/mod.rs:48-58), and only while the pool stays within its byte limit, 32 MiB per thread
    /// for every vector pool of 32-bit elements (<c>vec_vals</c>, <c>vec_cell_vals</c>, <c>rows</c>;
    /// pool/mod.rs:182-192, 424-430). The shared array pool is the .NET counterpart: thread-local and per-core caches,
    /// bucketed by power-of-two length so a request for a minimum length is served directly, and trimmed under memory
    /// pressure. Its arrays come back uncleared, which is what row storage wants.
    /// </para>
    /// <para>
    /// The byte limit is enforced per array: an array over <see cref="MaxPooledBytes"/> is never pooled, so it is
    /// freed when its owner lets go of it, as egglog frees a vector that would overflow its pool. The total the
    /// shared pool retains is bounded by its own per-bucket capacity and trimming rather than by a per-thread byte
    /// count, which it does not keep.
    /// </para>
    /// <para>
    /// Ownership is explicit: an array handed to <see cref="Release{T}"/> must not be used again by anyone. Growth
    /// (<see cref="Grow{T}"/>) never releases the array it replaces; the caller drops that array to the garbage
    /// collector, as Rust's <c>Vec</c> growth hands the old allocation back to the allocator rather than to a pool. A
    /// staging buffer (<see cref="RowBuffer.ForStaging(int)"/>), which nobody reads while it grows, releases it
    /// instead.
    /// </para>
    /// </remarks>
    internal static class PooledArray
    {

        /// <summary>
        /// The largest length that is allocated directly and never pooled. egglog: a <c>Vec</c> is reused only when its
        /// capacity exceeds 256 (pool/mod.rs:54-56).
        /// </summary>
        public const int MaxUnpooledLength = 256;

        /// <summary>
        /// The smallest length growth will allocate, so that tiny buffers do not reallocate on every row.
        /// </summary>
        public const int MinGrowLength = 16;

        /// <summary>
        /// The largest array, in bytes, that goes back to the shared pool: egglog's per-thread limit for its vector
        /// pools, <see cref="PoolLimits.VecCellValues"/> (32 MiB). <c>PoolState::push</c> drops a vector when the
        /// pool's bytes plus the vector's would exceed the limit (pool/mod.rs:186-188), so a vector of exactly the
        /// limit is still kept and a larger one never is.
        /// </summary>
        public const long MaxPooledBytes = PoolLimits.VecCellValues;

        /// <summary>
        /// Gets an array of at least <paramref name="minimumLength"/> elements. Lengths up to
        /// <see cref="MaxUnpooledLength"/> are allocated exactly. Longer ones are rented from the shared pool, which
        /// rounds them up to a power of two, unless the rounded array would be over <see cref="MaxPooledBytes"/>; such
        /// an array would never go back to the pool, so it is allocated at exactly the requested length instead. The
        /// contents are unspecified.
        /// </summary>
        /// <typeparam name="T">The element type.</typeparam>
        /// <param name="minimumLength">The minimum length.</param>
        /// <returns>The array, which the caller owns.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static T[] Allocate<T>(int minimumLength)
        {
            Debug.Assert(minimumLength >= 0);

            if (minimumLength == 0)
                return Array.Empty<T>();

            if (minimumLength <= MaxUnpooledLength)
                return new T[minimumLength];

            // Array.MaxLength is below 2^31, so the rounded length fits in a uint.
            if (IsPoolableLength<T>(BitOperations.RoundUpToPowerOf2((uint)minimumLength)))
                return ArrayPool<T>.Shared.Rent(minimumLength);

            return GC.AllocateUninitializedArray<T>(minimumLength);
        }

        /// <summary>
        /// Gives up an array obtained from <see cref="Allocate{T}"/> or <see cref="Grow{T}"/>. An array the shared pool
        /// can hold and that egglog would keep (longer than <see cref="MaxUnpooledLength"/>, a power-of-two length, at
        /// most <see cref="MaxPooledBytes"/>) goes back to the pool; any other is dropped. Arrays of reference-bearing
        /// types are cleared first so the pool does not keep their contents alive. O(1) for unmanaged types, except in
        /// Debug builds, where an unmanaged array going back to the pool is first filled with <see cref="PoisonByte"/>
        /// so that a read after release shows.
        /// </summary>
        /// <typeparam name="T">The element type.</typeparam>
        /// <param name="array">The array; nobody may use it again.</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Release<T>(T[] array)
        {
            if (IsPoolableLength<T>((uint)array.Length))
            {
                Poison(array);
                ArrayPool<T>.Shared.Return(array, RuntimeHelpers.IsReferenceOrContainsReferences<T>());
            }
        }

        /// <summary>
        /// The byte a Debug build fills released unmanaged arrays with. A cell of a released row then reads as
        /// 0xDDDDDDDD: not <see cref="Value.StaleRep"/>, so the row is not skipped as a tombstone, and far past any id
        /// a test allocates.
        /// </summary>
        internal const byte PoisonByte = 0xDD;

        /// <summary>
        /// In Debug builds, fills an unmanaged array about to go back to the pool with <see cref="PoisonByte"/>, so that
        /// code still reading it after <see cref="Release{T}"/> reads poison instead of the contents it expects.
        /// Pooled arrays are at most <see cref="MaxPooledBytes"/> bytes. Compiled out of Release builds.
        /// </summary>
        /// <typeparam name="T">The element type.</typeparam>
        /// <param name="array">The array being released.</param>
        [Conditional("DEBUG")]
        static void Poison<T>(T[] array)
        {
            if (RuntimeHelpers.IsReferenceOrContainsReferences<T>())
                return;

            ref var first = ref Unsafe.As<T, byte>(ref MemoryMarshal.GetArrayDataReference(array));
            Unsafe.InitBlockUnaligned(ref first, PoisonByte, (uint)(array.Length * Unsafe.SizeOf<T>()));
        }

        /// <summary>
        /// Allocates a replacement for <paramref name="array"/> of at least <paramref name="requiredLength"/> elements
        /// and copies its first <paramref name="usedLength"/> elements over. Growth is amortized: the new length is at
        /// least twice the old one, like <c>Vec::reserve</c>. O(<paramref name="usedLength"/>).
        /// </summary>
        /// <remarks>
        /// The old array is not released, and callers growing a buffer do not release it either: they drop it to the
        /// garbage collector. Whoever still holds it (an enumerator, a <c>Data</c> reference, or the row being appended
        /// when it lives in the buffer itself) keeps reading the old contents, and never an array the pool has handed
        /// to someone else. Only disposing a buffer returns its storage to the pool, except for a staging buffer
        /// (<see cref="RowBuffer.ForStaging(int)"/>), which nobody reads while it grows and which returns the old
        /// array once it has copied the rows over.
        /// </remarks>
        /// <typeparam name="T">The element type.</typeparam>
        /// <param name="array">The current array.</param>
        /// <param name="usedLength">How many leading elements to keep.</param>
        /// <param name="requiredLength">The minimum new length; at most <see cref="Array.MaxLength"/>.</param>
        /// <returns>The new array, which the caller owns.</returns>
        public static T[] Grow<T>(T[] array, int usedLength, long requiredLength)
        {
            Debug.Assert((uint)usedLength <= (uint)array.Length);
            Debug.Assert(requiredLength > array.Length);
            Debug.Assert(requiredLength <= Array.MaxLength);

            var length = Math.Max((long)array.Length * 2, Math.Max(requiredLength, MinGrowLength));
            if (length > Array.MaxLength)
                length = Array.MaxLength;

            var next = Allocate<T>((int)length);
            array.AsSpan(0, usedLength).CopyTo(next);
            return next;
        }

        /// <summary>
        /// Gets whether an array of <paramref name="length"/> elements of <typeparamref name="T"/> belongs in the
        /// shared pool: over <see cref="MaxUnpooledLength"/>, a power of two (the only lengths the shared pool accepts
        /// back), and within <see cref="MaxPooledBytes"/>. O(1).
        /// </summary>
        /// <typeparam name="T">The element type.</typeparam>
        /// <param name="length">The array length.</param>
        /// <returns><c>true</c> if <see cref="Release{T}"/> would return such an array to the pool.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool IsPoolable<T>(int length)
        {
            return IsPoolableLength<T>((uint)length);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static bool IsPoolableLength<T>(uint length)
        {
            return length > MaxUnpooledLength
                && BitOperations.IsPow2(length)
                && (long)length * Unsafe.SizeOf<T>() <= MaxPooledBytes;
        }

    }

}

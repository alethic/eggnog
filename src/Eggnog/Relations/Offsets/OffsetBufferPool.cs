using System;
using System.Buffers;
using System.Runtime.CompilerServices;

namespace Eggnog.Relations
{

    /// <summary>
    /// Rents and returns the <see cref="int"/> buffers that back sparse subsets and sorted offset vectors. It stands
    /// in for egglog's thread-local <c>Pool&lt;SortedOffsetVector&gt;</c> (core-relations/src/pool/mod.rs:431) and is
    /// backed by <see cref="ArrayPool{T}.Shared"/>, with a thread-static <see cref="OffsetBufferCache"/> in front for
    /// buffers of at most <see cref="OffsetBufferCache.MaxLength"/> row ids.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Ownership is explicit: whoever rents a buffer returns it exactly once, through <see cref="Return"/>.
    /// A request for zero elements gets <see cref="Array.Empty{T}"/>, which is never handed to the pool, so an empty
    /// sparse subset costs nothing. Rented buffers are not cleared and are usually longer than requested; callers
    /// track their own element count. All members are thread-safe.
    /// </para>
    /// <para>
    /// The cache bounds what each thread keeps by bytes (<see cref="PoolLimits.OffsetVec"/> per length), as egglog
    /// bounds its pool, rather than by count, as the shared array pool does: with the shared pool alone, a join holding
    /// a few hundred small refined subsets at once allocates on every rent past the pool's capacity, which is a few
    /// dozen buffers of a length per core. Every buffer, cached or not, has a length the shared pool hands out, so
    /// either side takes the other's buffers.
    /// </para>
    /// </remarks>
    internal static class OffsetBufferPool
    {

        /// <summary>
        /// The calling thread's cache of small buffers, created on its first return.
        /// </summary>
        [ThreadStatic]
        static OffsetBufferCache? cache;

        /// <summary>
        /// Rents a buffer with room for at least <paramref name="minimumLength"/> row ids. Its contents are
        /// unspecified.
        /// </summary>
        /// <param name="minimumLength">The minimum length; zero returns the shared empty array.</param>
        /// <returns>The buffer.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static int[] Rent(int minimumLength)
        {
            if (minimumLength == 0)
                return Array.Empty<int>();

            // A negative length skips the cache and makes the shared pool throw.
            if ((uint)minimumLength <= OffsetBufferCache.MaxLength && cache?.TryRent(minimumLength) is { } cached)
                return cached;

            return ArrayPool<int>.Shared.Rent(minimumLength);
        }

        /// <summary>
        /// Returns a buffer obtained from <see cref="Rent"/>, <see cref="RentCopy"/> or <see cref="GrowForAppend"/>.
        /// The caller must not touch it afterwards.
        /// </summary>
        /// <param name="buffer">The buffer.</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static void Return(int[] buffer)
        {
            if (buffer.Length == 0)
                return;

            if (buffer.Length <= OffsetBufferCache.MaxLength && (cache ??= new OffsetBufferCache()).TryReturn(buffer))
                return;

            ArrayPool<int>.Shared.Return(buffer);
        }

        /// <summary>
        /// Rents a buffer and copies <c>source[start..end)</c> to its start. O(<paramref name="end"/> -
        /// <paramref name="start"/>).
        /// </summary>
        /// <param name="source">The array to copy from.</param>
        /// <param name="start">The first position to copy.</param>
        /// <param name="end">One past the last position to copy.</param>
        /// <returns>The new buffer, holding <c>end - start</c> row ids at positions <c>[0, end - start)</c>.</returns>
        internal static int[] RentCopy(int[] source, int start, int end)
        {
            var length = end - start;
            var buffer = Rent(length);
            Array.Copy(source, start, buffer, 0, length);
            return buffer;
        }

        /// <summary>
        /// Makes room to append one row id to the run <c>buffer[start..end)</c>, whose end has reached the end of
        /// the buffer. If at most half the buffer is in use and the run does not start at zero, the run moves down to
        /// position zero in place; otherwise a buffer of twice the length is rented, the run copied to its start and
        /// the old buffer returned. Amortized O(1) per append, like <c>Vec::push</c>.
        /// </summary>
        /// <param name="buffer">The full buffer, owned by the caller.</param>
        /// <param name="start">The run's first position; updated.</param>
        /// <param name="end">One past the run's last position; updated.</param>
        /// <returns>The buffer to use from now on, with <c>end &lt; Length</c>.</returns>
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static int[] GrowForAppend(int[] buffer, ref int start, ref int end)
        {
            var length = end - start;
            if (start > 0 && length <= buffer.Length >> 1)
            {
                Array.Copy(buffer, start, buffer, 0, length);
                start = 0;
                end = length;
                return buffer;
            }

            var grown = Rent((int)Math.Min(Array.MaxLength, Math.Max(length + 1L, buffer.Length * 2L)));
            Array.Copy(buffer, start, grown, 0, length);
            Return(buffer);
            start = 0;
            end = length;
            return grown;
        }

    }

}

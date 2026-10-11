using System;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace Eggnog.Relations
{

    /// <summary>
    /// One thread's stock of returned offset buffers, in front of <see cref="System.Buffers.ArrayPool{T}.Shared"/>:
    /// the per-thread half of <see cref="OffsetBufferPool"/>, standing in for egglog's thread-local <c>offset_vec</c>
    /// pool (core-relations/src/pool/mod.rs:431), which keeps returned <c>SortedOffsetVector</c>s up to
    /// <see cref="PoolLimits.OffsetVec"/> bytes per thread.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The shared array pool keeps a fixed number of arrays per size (a few dozen per core), so a join that holds
    /// hundreds of small refined subsets at once (a morsel of frames, each owning the rows of its probes) exhausts it,
    /// and every rent past that allocates while every return past it is dropped. egglog's pool is bounded by bytes
    /// instead: one MiB holds thousands of small vectors. This cache restores that bound for buffers of at most
    /// <see cref="MaxLength"/> row ids.
    /// </para>
    /// <para>
    /// Layout: one last-in first-out stack per power-of-two length from <see cref="MinLength"/> to
    /// <see cref="MaxLength"/> (the lengths the shared pool hands out). Each stack holds at most
    /// <see cref="PoolLimits.OffsetVec"/> bytes, weighing a buffer as its length times four as egglog weighs a vector
    /// by its capacity: 16,384 buffers of the shortest length, 64 of the longest. egglog's one budget is shared by all
    /// lengths; one per length keeps a burst of long buffers from crowding out the short ones the join holds by the
    /// hundred, at the cost of up to nine budgets per thread.
    /// </para>
    /// <para>
    /// Not thread-safe: each thread owns its cache (<see cref="OffsetBufferPool"/> keeps it thread-static), so a buffer
    /// rented on one thread and returned on another lands in the second thread's cache, as with egglog's thread-local
    /// pools. Rent and return are O(1); the stacks grow by doubling up to their budgets.
    /// </para>
    /// </remarks>
    internal sealed class OffsetBufferCache
    {

        /// <summary>
        /// The shortest buffer the shared pool hands out, and the shortest cached.
        /// </summary>
        public const int MinLength = 16;

        /// <summary>
        /// The longest buffer cached; longer ones go straight to the shared pool, which holds the few that are live
        /// at once.
        /// </summary>
        public const int MaxLength = 4096;

        /// <summary>
        /// The base-two logarithm of <see cref="MinLength"/>.
        /// </summary>
        const int MinClass = 4;

        /// <summary>
        /// The number of cached lengths, <see cref="MinLength"/> to <see cref="MaxLength"/>.
        /// </summary>
        const int ClassCount = 9;

        readonly int[]?[][] stacks = new int[]?[ClassCount][];
        readonly int[] counts = new int[ClassCount];

        /// <summary>
        /// Gets the bytes of buffers held, weighed as their lengths times four. O(lengths).
        /// </summary>
        public long Bytes
        {
            get
            {
                long total = 0;
                for (var c = 0; c < ClassCount; c++)
                    total += (long)counts[c] * LengthOf(c) * sizeof(int);

                return total;
            }
        }

        /// <summary>
        /// Gets the most buffers of <paramref name="length"/> row ids the cache keeps: a budget of
        /// <see cref="PoolLimits.OffsetVec"/> bytes divided by the buffers' weight.
        /// </summary>
        /// <param name="length">A power of two from <see cref="MinLength"/> to <see cref="MaxLength"/>.</param>
        /// <returns>The capacity.</returns>
        public static int CapacityFor(int length) => Capacity(ClassOf(length));

        /// <summary>
        /// Takes a cached buffer of at least <paramref name="minimumLength"/> row ids, of the length the shared pool
        /// would hand out for it.
        /// </summary>
        /// <param name="minimumLength">The minimum length; in <c>[1, MaxLength]</c>.</param>
        /// <returns>The buffer, or <c>null</c> if none of that length is cached.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int[]? TryRent(int minimumLength)
        {
            Debug.Assert(minimumLength >= 1 && minimumLength <= MaxLength);
            var sizeClass = ClassOf(minimumLength);
            var count = counts[sizeClass];
            if (count == 0)
                return null;

            var stack = stacks[sizeClass];
            var buffer = stack[--count]!;
            stack[count] = null;
            counts[sizeClass] = count;
            return buffer;
        }

        /// <summary>
        /// Keeps <paramref name="buffer"/> if its length is one the cache holds and its stack is under budget.
        /// </summary>
        /// <param name="buffer">The buffer, which the caller gives up if this returns <c>true</c>.</param>
        /// <returns><c>true</c> if the buffer was kept.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool TryReturn(int[] buffer)
        {
            var length = buffer.Length;
            if (length < MinLength || length > MaxLength || BitOperations.IsPow2(length) == false)
                return false;

            var sizeClass = ClassOf(length);
            var count = counts[sizeClass];
            if (count >= Capacity(sizeClass))
                return false;

            var stack = stacks[sizeClass];
            if (stack is null || count == stack.Length)
                stack = Grow(sizeClass);

            Debug.Assert(Array.IndexOf(stack, buffer, 0, count) < 0, "an offset buffer was returned twice");
            stack[count] = buffer;
            counts[sizeClass] = count + 1;
            return true;
        }

        /// <summary>
        /// Gets the stack of buffers of <paramref name="length"/> row ids rounded up to a power of two:
        /// <c>log2(next_pow2(length)) - 4</c>, at least zero.
        /// </summary>
        /// <param name="length">The length; in <c>[1, MaxLength]</c>.</param>
        /// <returns>The stack's index.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static int ClassOf(int length) => length <= MinLength ? 0 : 32 - BitOperations.LeadingZeroCount((uint)length - 1) - MinClass;

        /// <summary>
        /// Gets the length of the buffers of stack <paramref name="sizeClass"/>.
        /// </summary>
        /// <param name="sizeClass">The stack's index.</param>
        /// <returns>The length.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static int LengthOf(int sizeClass) => MinLength << sizeClass;

        /// <summary>
        /// Gets the most buffers stack <paramref name="sizeClass"/> keeps: <see cref="PoolLimits.OffsetVec"/> bytes
        /// divided by the weight of its buffers.
        /// </summary>
        /// <param name="sizeClass">The stack's index.</param>
        /// <returns>The capacity.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static int Capacity(int sizeClass) => (int)(PoolLimits.OffsetVec / ((long)LengthOf(sizeClass) * sizeof(int)));

        /// <summary>
        /// Doubles the stack of <paramref name="sizeClass"/>, or creates it.
        /// </summary>
        /// <param name="sizeClass">The stack's index.</param>
        /// <returns>The new stack.</returns>
        [MethodImpl(MethodImplOptions.NoInlining)]
        int[]?[] Grow(int sizeClass)
        {
            int[]?[]? old = stacks[sizeClass];
            var grown = new int[]?[Math.Min(old is null ? 16 : old.Length * 2, Capacity(sizeClass))];
            old?.CopyTo(grown, 0);
            stacks[sizeClass] = grown;
            return grown;
        }

    }

}

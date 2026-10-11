using System;
using System.Runtime.CompilerServices;

namespace Eggnog.Relations
{

    /// <summary>
    /// The run-time order of a stage list: position <c>i</c> runs stage <c>this[i]</c>. egglog's <c>InstrOrder</c>
    /// (core-relations/src/free_join/execute.rs:2507-2532), a permutation of <c>u16</c> stage indexes, so a stage list
    /// has at most <see cref="MaxStages"/> stages.
    /// </summary>
    /// <remarks>
    /// Owned by one run (cloned when a run forks work to another thread); <see cref="StageOrdering"/> permutes it.
    /// Not thread-safe.
    /// </remarks>
    internal sealed class InstrOrder
    {

        /// <summary>
        /// The largest number of stages an order can hold (<see cref="ushort.MaxValue"/>).
        /// </summary>
        public const int MaxStages = ushort.MaxValue;

        ushort[] data;
        int count;

        /// <summary>
        /// Initializes the identity order over <paramref name="count"/> stages. egglog's
        /// <c>InstrOrder::from_iter(0..count)</c>.
        /// </summary>
        /// <param name="count">The number of stages.</param>
        /// <exception cref="InvalidOperationException">More than <see cref="MaxStages"/> stages.</exception>
        public InstrOrder(int count)
        {
            data = Array.Empty<ushort>();
            Reset(count);
        }

        InstrOrder(ushort[] data, int count)
        {
            this.data = data;
            this.count = count;
        }

        /// <summary>
        /// Gets the number of stages.
        /// </summary>
        public int Count => count;

        /// <summary>
        /// Gets the stage index scheduled at <paramref name="position"/>. egglog's <c>get</c>.
        /// </summary>
        /// <param name="position">The position.</param>
        public int this[int position]
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get
            {
                if ((uint)position >= (uint)count)
                    throw new ArgumentOutOfRangeException(nameof(position));

                return data[position];
            }
        }

        /// <summary>
        /// Resets to the identity order over <paramref name="count"/> stages, reusing the buffer when it is large enough.
        /// </summary>
        /// <param name="count">The number of stages.</param>
        /// <exception cref="InvalidOperationException">More than <see cref="MaxStages"/> stages.</exception>
        public void Reset(int count)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(count);
            if (count > MaxStages)
                throw new InvalidOperationException("Too many instructions: a stage list holds at most 65535 stages.");

            if (data.Length < count)
                data = new ushort[count];

            for (var i = 0; i < count; i++)
                data[i] = (ushort)i;

            this.count = count;
        }

        /// <summary>
        /// Swaps the stages at two positions.
        /// </summary>
        /// <param name="i">One position.</param>
        /// <param name="j">The other position.</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Swap(int i, int j)
        {
            if ((uint)i >= (uint)count || (uint)j >= (uint)count)
                throw new ArgumentOutOfRangeException(nameof(i));

            (data[i], data[j]) = (data[j], data[i]);
        }

        /// <summary>
        /// Creates an independent copy.
        /// </summary>
        /// <returns>The copy.</returns>
        public InstrOrder Clone() => new(data.AsSpan(0, count).ToArray(), count);

        /// <summary>
        /// Copies <paramref name="other"/> into this order, reusing the buffer when it is large enough.
        /// </summary>
        /// <param name="other">The order to copy.</param>
        public void CopyFrom(InstrOrder other)
        {
            ArgumentNullException.ThrowIfNull(other);
            if (data.Length < other.count)
                data = new ushort[other.count];

            other.data.AsSpan(0, other.count).CopyTo(data);
            count = other.count;
        }

        /// <summary>
        /// Copies the order into an array of stage indexes, for diagnostics and tests.
        /// </summary>
        /// <returns>The stage index at each position.</returns>
        public int[] ToArray()
        {
            var result = new int[count];
            for (var i = 0; i < count; i++)
                result[i] = data[i];

            return result;
        }

        /// <inheritdoc />
        public override string ToString() => "[" + string.Join(", ", ToArray()) + "]";

    }

}

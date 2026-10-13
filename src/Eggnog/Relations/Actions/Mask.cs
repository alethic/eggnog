using System;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace Eggnog.Relations
{

    /// <summary>
    /// The lanes of an action batch that are still live: egglog's <c>Mask</c> (core-relations/src/action/mask.rs:13-108)
    /// together with the consumers of its <c>MaskIter</c> protocol (<c>retain</c>, <c>fill_vec</c>,
    /// <c>assign_vec_and_retain</c>, <c>for_each</c>; mask.rs:110-245).
    /// </summary>
    /// <remarks>
    /// <para>
    /// A batch never holds more than <see cref="MaxLanes"/> matches (egglog's <c>VAR_BATCH_SIZE</c>,
    /// core-relations/src/free_join/execute.rs:1807), so the mask is two 64-bit words and a length instead of egglog's
    /// pooled <c>FixedBitSet</c>: copying it is egglog's <c>clone</c> and allocates nothing (egglog's bitset pooling,
    /// in core-relations/src/pool/mod.rs, is a no-op anyway). Bits at or above <see cref="Length"/> are never set.
    /// </para>
    /// <para>
    /// Iteration visits only live lanes, with <see cref="BitOperations.TrailingZeroCount(ulong)"/> and
    /// <c>w &amp;= w - 1</c>: O(live lanes) where egglog's <c>get_at</c> walk is O(<see cref="Length"/>). Lanes are
    /// visited in ascending order, as egglog does, so side effects of lane callbacks (counter increments, external
    /// function calls) happen in the same order. Each consumer iterates a snapshot of the live lanes taken when it
    /// starts, and failing lanes are cleared in the mask itself.
    /// </para>
    /// <para>
    /// A mutable struct: callers keep it in a local and pass it by reference. The consumers are generic over struct
    /// callbacks (<see cref="ILaneFilter"/>, <see cref="ILaneProducer"/>, <see cref="ILaneVisitor"/>), so each lane loop
    /// is specialized for its callback with no virtual or delegate call, as Rust monomorphizes the closures. Compiled
    /// plans can keep the two words in locals instead (<see cref="Low"/>, <see cref="High"/>).
    /// </para>
    /// </remarks>
    internal struct Mask
    {

        /// <summary>
        /// The largest number of lanes a mask covers: the batch size of an action buffer.
        /// </summary>
        public const int MaxLanes = 128;

        ulong low;
        ulong high;
        int length;

        /// <summary>
        /// Initializes a mask of <paramref name="length"/> lanes, all live: egglog's <c>Mask::new(0..length)</c>.
        /// </summary>
        /// <param name="length">The number of lanes, at most <see cref="MaxLanes"/>.</param>
        public Mask(int length)
            : this(0, length)
        {

        }

        /// <summary>
        /// Initializes a mask of <paramref name="end"/> lanes in which exactly the lanes <c>[start, end)</c> are live:
        /// egglog's <c>Mask::new(start..end)</c>, which grows the bitset to <paramref name="end"/> bits.
        /// </summary>
        /// <param name="start">The first live lane.</param>
        /// <param name="end">The number of lanes, at most <see cref="MaxLanes"/>.</param>
        public Mask(int start, int end)
        {
            if ((uint)end > MaxLanes)
                throw new ArgumentOutOfRangeException(nameof(end), end, $"A mask covers at most {MaxLanes} lanes.");
            if ((uint)start > (uint)end)
                throw new ArgumentOutOfRangeException(nameof(start), start, "The range is empty or reversed.");

            low = RangeBits(start, end, 0);
            high = RangeBits(start, end, 64);
            length = end;
        }

        /// <summary>
        /// Gets the number of lanes, live or not: egglog's <c>len</c>, the batch's match count.
        /// </summary>
        public readonly int Length => length;

        /// <summary>
        /// Gets whether no lane is live. egglog's <c>is_empty</c>.
        /// </summary>
        public readonly bool IsEmpty
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => (low | high) == 0;
        }

        /// <summary>
        /// Gets the number of live lanes. egglog's <c>count_ones</c>. Two population counts.
        /// </summary>
        public readonly int Count => BitOperations.PopCount(low) + BitOperations.PopCount(high);

        /// <summary>
        /// Gets the live lanes below 64, one bit per lane.
        /// </summary>
        public readonly ulong Low => low;

        /// <summary>
        /// Gets the live lanes from 64 up, one bit per lane (bit <c>i</c> is lane <c>64 + i</c>).
        /// </summary>
        public readonly ulong High => high;

        /// <summary>
        /// Gets whether <paramref name="lane"/> is live. Lanes outside the mask are not.
        /// </summary>
        /// <param name="lane">The lane.</param>
        /// <returns><c>true</c> if live.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public readonly bool Contains(int lane)
        {
            if ((uint)lane >= (uint)length)
                return false;

            return ((lane < 64 ? low : high) & (1UL << lane)) != 0;
        }

        /// <summary>
        /// Drops <paramref name="lane"/>. egglog's <c>MaskIter::remove</c> (a <c>set(idx, false)</c>).
        /// </summary>
        /// <param name="lane">A lane below <see cref="Length"/>.</param>
        public void Remove(int lane)
        {
            if ((uint)lane >= (uint)length)
                ThrowLane(lane);

            if (lane < 64)
                low &= ~(1UL << lane);
            else
                high &= ~(1UL << lane);
        }

        /// <summary>
        /// Drops every lane; the length stays. egglog's <c>clear</c>.
        /// </summary>
        public void Clear()
        {
            low = 0;
            high = 0;
        }

        /// <summary>
        /// Replaces this mask by the lanes live in exactly one of it and <paramref name="other"/>. egglog's
        /// <c>symmetric_difference</c>, which debug-asserts that this mask is a subset of <paramref name="other"/>, so
        /// the result is the lanes of <paramref name="other"/> this mask lacks: the lanes a lookup or a call failed.
        /// </summary>
        /// <param name="other">A superset of this mask.</param>
        public void SymmetricDifference(in Mask other)
        {
            Debug.Assert(IsSubsetOf(other), "the mask must be a subset of the other");
            low ^= other.low;
            high ^= other.high;
            length = Math.Max(length, other.length);
        }

        /// <summary>
        /// Adds the lanes live in <paramref name="other"/>. egglog's <c>union</c>.
        /// </summary>
        /// <param name="other">The other mask.</param>
        public void Union(in Mask other)
        {
            low |= other.low;
            high |= other.high;
            length = Math.Max(length, other.length);
        }

        /// <summary>
        /// Gets whether every lane live here is live in <paramref name="other"/>.
        /// </summary>
        /// <param name="other">The other mask.</param>
        /// <returns><c>true</c> if this mask is a subset.</returns>
        public readonly bool IsSubsetOf(in Mask other) => (low & ~other.low) == 0 && (high & ~other.high) == 0;

        /// <summary>
        /// Enumerates the live lanes in ascending order, from a snapshot taken now.
        /// </summary>
        /// <returns>The enumerator.</returns>
        public readonly MaskEnumerator GetEnumerator() => new(low, high);

        /// <summary>
        /// Drops every live lane <paramref name="filter"/> rejects. egglog's <c>MaskIter::retain</c>.
        /// </summary>
        /// <typeparam name="TFilter">The filter type; a struct, so the loop is specialized for it.</typeparam>
        /// <param name="filter">Decides which lanes stay.</param>
        public void Retain<TFilter>(ref TFilter filter)
            where TFilter : struct, ILaneFilter
        {
            low = RetainWord(low, 0, ref filter);
            high = RetainWord(high, 64, ref filter);
        }

        /// <summary>
        /// Calls <paramref name="visitor"/> on every live lane. egglog's <c>MaskIter::for_each</c>.
        /// </summary>
        /// <typeparam name="TVisitor">The visitor type; a struct, so the loop is specialized for it.</typeparam>
        /// <param name="visitor">The visitor.</param>
        public readonly void ForEach<TVisitor>(ref TVisitor visitor)
            where TVisitor : struct, ILaneVisitor
        {
            for (var bits = low; bits != 0; bits &= bits - 1)
                visitor.Visit(BitOperations.TrailingZeroCount(bits));

            for (var bits = high; bits != 0; bits &= bits - 1)
                visitor.Visit(64 + BitOperations.TrailingZeroCount(bits));
        }

        /// <summary>
        /// Writes one value per lane to <paramref name="output"/>[<paramref name="offset"/> + lane] for every lane
        /// below <see cref="Length"/>: what <paramref name="producer"/> produces for a live lane, and
        /// <paramref name="missing"/> for a dead lane or a lane the producer fails, which is dropped. egglog's
        /// <c>MaskIter::fill_vec</c>, writing into a column instead of pushing onto a vector.
        /// </summary>
        /// <typeparam name="TProducer">The producer type; a struct, so the loop is specialized for it.</typeparam>
        /// <param name="producer">Produces the value of each live lane.</param>
        /// <param name="output">The array holding the column.</param>
        /// <param name="offset">The index of lane 0 in <paramref name="output"/>.</param>
        /// <param name="missing">The value of dead and failed lanes (egglog passes <c>Value::stale</c>).</param>
        public void Fill<TProducer>(ref TProducer producer, uint[] output, int offset, uint missing)
            where TProducer : struct, ILaneProducer
        {
            // One explicit range check covers every lane below the length; the lane loops then write unchecked.
            output.AsSpan(offset, length).Fill(missing);
            ref var column = ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(output), offset);
            low = ProduceWord(low, 0, ref producer, ref column);
            high = ProduceWord(high, 64, ref producer, ref column);
        }

        /// <summary>
        /// Overwrites <paramref name="output"/>[<paramref name="offset"/> + lane] with what
        /// <paramref name="producer"/> produces for each live lane, and drops the lanes it fails, whose cells are left
        /// alone. egglog's <c>MaskIter::assign_vec_and_retain</c>; with a producer that never fails it is
        /// <c>assign_vec</c>.
        /// </summary>
        /// <typeparam name="TProducer">The producer type; a struct, so the loop is specialized for it.</typeparam>
        /// <param name="producer">Produces the value of each live lane.</param>
        /// <param name="output">The array holding the column.</param>
        /// <param name="offset">The index of lane 0 in <paramref name="output"/>.</param>
        public void AssignAndRetain<TProducer>(ref TProducer producer, uint[] output, int offset)
            where TProducer : struct, ILaneProducer
        {
            _ = output.AsSpan(offset, length);
            ref var column = ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(output), offset);
            low = ProduceWord(low, 0, ref producer, ref column);
            high = ProduceWord(high, 64, ref producer, ref column);
        }

        /// <summary>
        /// Formats the live lanes, as <c>{0, 2, 5}/8</c>.
        /// </summary>
        /// <returns>The text.</returns>
        public override readonly string ToString()
        {
            var b = new StringBuilder("{");
            var first = true;
            foreach (var lane in this)
            {
                if (first == false)
                    b.Append(", ");

                b.Append(lane);
                first = false;
            }

            return b.Append("}/").Append(length).ToString();
        }

        /// <summary>
        /// Runs <paramref name="filter"/> over the lanes of one word and returns the word with the rejected lanes
        /// cleared.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static ulong RetainWord<TFilter>(ulong word, int laneBase, ref TFilter filter)
            where TFilter : struct, ILaneFilter
        {
            var result = word;
            for (var bits = word; bits != 0; bits &= bits - 1)
                if (filter.Keep(laneBase + BitOperations.TrailingZeroCount(bits)) == false)
                    result &= ~(bits & (0UL - bits));

            return result;
        }

        /// <summary>
        /// Runs <paramref name="producer"/> over the lanes of one word, storing successes in the column, and returns
        /// the word with the failed lanes cleared. The caller has checked that the column holds every lane.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static ulong ProduceWord<TProducer>(ulong word, int laneBase, ref TProducer producer, ref uint column)
            where TProducer : struct, ILaneProducer
        {
            var result = word;
            for (var bits = word; bits != 0; bits &= bits - 1)
            {
                var lane = laneBase + BitOperations.TrailingZeroCount(bits);
                if (producer.TryProduce(lane, out var value))
                    Unsafe.Add(ref column, lane) = value;
                else
                    result &= ~(bits & (0UL - bits));
            }

            return result;
        }

        /// <summary>
        /// Gets the bits of the lanes <c>[start, end)</c> that fall in the word covering lanes
        /// <c>[laneBase, laneBase + 64)</c>.
        /// </summary>
        static ulong RangeBits(int start, int end, int laneBase)
        {
            var lo = Math.Max(start, laneBase) - laneBase;
            var hi = Math.Min(end, laneBase + 64) - laneBase;
            if (lo >= hi)
                return 0;

            var width = hi - lo;
            var bits = width == 64 ? ulong.MaxValue : (1UL << width) - 1;
            return bits << lo;
        }

        [DoesNotReturn]
        [MethodImpl(MethodImplOptions.NoInlining)]
        readonly void ThrowLane(int lane)
        {
            throw new ArgumentOutOfRangeException(nameof(lane), lane, $"The mask has {length} lanes.");
        }

    }

}

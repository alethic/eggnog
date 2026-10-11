using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Eggnog.Relations
{

    /// <summary>
    /// The adaptive LSD radix sort behind the sort-based index build: egglog's <c>radix_sort_pairs_by_value</c>
    /// (core-relations/src/hash_index/mod.rs:422-488), over (value, row) pairs packed into <see cref="ulong"/>s as
    /// <c>value &lt;&lt; 32 | row</c> (<see cref="ColumnPairSink.Pack"/>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Sorts stably by the value half only, in one to four passes of eight bits chosen from the largest value, so only
    /// the bits in use cost anything. Pairs arrive in non-decreasing row order (<see cref="ColumnPairSink"/> emits them
    /// row by row), and stability keeps that order within each value, so the result is sorted by the whole packed word:
    /// by (value, row). That is the fix for egglog's multi-column build (map §8.1 row 8), which collects pairs column by
    /// column and therefore leaves a value's rows unsorted.
    /// </para>
    /// <para>
    /// Below <see cref="SmallSortThreshold"/> pairs it sorts the packed words directly, as egglog falls back to
    /// <c>sort_unstable</c> on whole tuples. Unlike egglog, which recounts each pass, all four histograms come from one
    /// read of the input, and a pass whose byte is the same in every pair is skipped. O(n × passes), one pooled scratch
    /// array of n words.
    /// </para>
    /// </remarks>
    internal static class PairRadixSort
    {

        /// <summary>
        /// Below this many pairs the sort is a comparison sort: egglog's threshold.
        /// </summary>
        internal const int SmallSortThreshold = 64;

        /// <summary>
        /// Sorts <c>pairs[0..count)</c> by value, stably. When the input is in non-decreasing row order the result is
        /// sorted by (value, row).
        /// </summary>
        /// <param name="pairs">The packed pairs.</param>
        /// <param name="count">The number of pairs.</param>
        public static void SortByValue(ulong[] pairs, int count)
        {
            if ((uint)count > (uint)pairs.Length)
                throw new ArgumentOutOfRangeException(nameof(count));

            if (count < SmallSortThreshold)
            {
                pairs.AsSpan(0, count).Sort();
                return;
            }

            // One read builds the histograms of all four value bytes.
            Span<int> histograms = stackalloc int[4 * 256];
            histograms.Clear();
            uint max = 0;
            foreach (var pair in pairs.AsSpan(0, count))
            {
                var value = (uint)(pair >> 32);
                max |= value;
                histograms[(int)(value & 0xFF)]++;
                histograms[256 + (int)((value >> 8) & 0xFF)]++;
                histograms[512 + (int)((value >> 16) & 0xFF)]++;
                histograms[768 + (int)(value >> 24)]++;
            }

            // egglog's pass count: the bytes the largest value uses (the OR of all values has the same top byte).
            var passes = max < (1u << 8) ? 1 : max < (1u << 16) ? 2 : max < (1u << 24) ? 3 : 4;
            var scratch = PooledArray.Allocate<ulong>(count);
            var source = pairs;
            var destination = scratch;
            for (var pass = 0; pass < passes; pass++)
            {
                var histogram = histograms.Slice(pass * 256, 256);
                var shift = 32 + pass * 8;

                // Every pair has the same byte here: the pass would copy the input unchanged.
                if (histogram[(int)((source[0] >> shift) & 0xFF)] == count)
                    continue;

                var offset = 0;
                for (var bucket = 0; bucket < 256; bucket++)
                {
                    var n = histogram[bucket];
                    histogram[bucket] = offset;
                    offset += n;
                }

                Scatter(source, destination, count, shift, histogram);
                (source, destination) = (destination, source);
            }

            if (ReferenceEquals(source, pairs) == false)
                source.AsSpan(0, count).CopyTo(pairs);

            PooledArray.Release(scratch);
            AssertSortedByValue(pairs, count);
        }

        /// <summary>
        /// One stable counting-sort pass: moves each pair to the next free position of its bucket.
        /// </summary>
        /// <param name="source">The input pairs.</param>
        /// <param name="destination">Receives the pairs; at least <paramref name="count"/> long.</param>
        /// <param name="count">The number of pairs.</param>
        /// <param name="shift">The bit position of the byte that picks the bucket.</param>
        /// <param name="offsets">The first position of each of the 256 buckets; advanced as pairs are placed.</param>
        static void Scatter(ulong[] source, ulong[] destination, int count, int shift, Span<int> offsets)
        {
            // The offsets are exclusive prefix sums of a histogram of these very pairs, so every write lands in
            // [0, count) and the unchecked writes below cannot leave the destination, which is at least count long.
            Debug.Assert(source.Length >= count && destination.Length >= count);
            ref var src = ref MemoryMarshal.GetArrayDataReference(source);
            ref var dst = ref MemoryMarshal.GetArrayDataReference(destination);
            ref var off = ref MemoryMarshal.GetReference(offsets);
            for (var i = 0; i < count; i++)
            {
                var pair = Unsafe.Add(ref src, i);
                ref var slot = ref Unsafe.Add(ref off, (int)((pair >> shift) & 0xFF));
                Unsafe.Add(ref dst, slot) = pair;
                slot++;
            }
        }

        /// <summary>
        /// Debug builds: checks that the values are non-decreasing.
        /// </summary>
        [Conditional("DEBUG")]
        static void AssertSortedByValue(ulong[] pairs, int count)
        {
            for (var i = 1; i < count; i++)
                Debug.Assert((pairs[i - 1] >> 32) <= (pairs[i] >> 32), "the radix sort left values out of order");
        }

    }

}

using System;
using System.Collections.Generic;
using System.Linq;

using Xunit;

using static Eggnog.Relations.Tests.OffsetTestUtil;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// Tests for the <see cref="SortedOffsets"/> kernels against linear reference implementations, including each
    /// intersection strategy on inputs its selector would not pick.
    /// </summary>
    public class SortedOffsetsTests
    {

        static readonly Func<int[], int, int, int[], int, int, int>[] kernels =
        [
            SortedOffsets.IntersectInPlace,
            SortedOffsets.IntersectByMerge,
            SortedOffsets.IntersectByGallopInCur,
            SortedOffsets.IntersectByGallopInOther,
        ];

        /// <summary>
        /// Places <paramref name="rows"/> at a random position inside a larger array, surrounded by junk, so kernels
        /// are exercised on runs that neither start at zero nor end at the array's end.
        /// </summary>
        static int[] Embed(Random random, IReadOnlyList<int> rows, out int start)
        {
            start = random.Next(0, 5);
            var tail = random.Next(0, 5);
            var array = new int[start + rows.Count + tail];
            for (var i = 0; i < array.Length; i++)
                array[i] = random.Next();

            for (var i = 0; i < rows.Count; i++)
                array[start + i] = rows[i];

            return array;
        }

        [Fact]
        public void LowerBoundMatchesLinearScan()
        {
            var random = new Random(42);
            for (var trial = 0; trial < 500; trial++)
            {
                var rows = RandomSorted(random, random.Next(0, 120), random.Next(1, 300), duplicates: trial % 3 != 0);
                var array = Embed(random, rows, out var start);
                for (var probe = 0; probe < 25; probe++)
                {
                    var target = random.Next(-1, 310);
                    var expected = 0;
                    while (expected < rows.Count && rows[expected] < target)
                        expected++;

                    Assert.Equal(start + expected, SortedOffsets.LowerBound(array, start, start + rows.Count, target));
                }
            }
        }

        [Fact]
        public void LowerBoundHandlesExtremes()
        {
            var array = new[] { int.MinValue, -1, 0, int.MaxValue };
            Assert.Equal(0, SortedOffsets.LowerBound(array, 0, 4, int.MinValue));
            Assert.Equal(3, SortedOffsets.LowerBound(array, 0, 4, 1));
            Assert.Equal(3, SortedOffsets.LowerBound(array, 0, 4, int.MaxValue));
            Assert.Equal(2, SortedOffsets.LowerBound(array, 2, 2, 5));
            Assert.Throws<ArgumentOutOfRangeException>(() => SortedOffsets.LowerBound(array, 3, 5, 0));
            Assert.Throws<ArgumentOutOfRangeException>(() => SortedOffsets.LowerBound(array, -1, 2, 0));
        }

        [Fact]
        public void GallopMatchesLinearScan()
        {
            var random = new Random(7);
            for (var trial = 0; trial < 500; trial++)
            {
                var rows = RandomSorted(random, random.Next(0, 300), random.Next(1, 600), duplicates: trial % 2 == 0);
                var array = Embed(random, rows, out var start);
                var end = start + rows.Count;
                for (var probe = 0; probe < 25; probe++)
                {
                    var from = start + random.Next(0, rows.Count + 3);
                    var target = random.Next(-1, 610);
                    var expected = Math.Min(from, end);
                    while (expected < end && array[expected] < target)
                        expected++;

                    var found = SortedOffsets.Gallop(array, from, end, target, out var index);
                    Assert.Equal(expected, index);
                    Assert.Equal(expected < end && array[expected] == target, found);
                }
            }
        }

        [Fact]
        public void GallopFindsFirstCopyAcrossLongRuns()
        {
            var array = Enumerable.Repeat(1, 3).Concat(Enumerable.Repeat(5, 1000)).Concat([9]).ToArray();
            Assert.True(SortedOffsets.Gallop(array, 0, array.Length, 5, out var index));
            Assert.Equal(3, index);
            Assert.True(SortedOffsets.Gallop(array, 500, array.Length, 5, out index));
            Assert.Equal(500, index);
            Assert.True(SortedOffsets.Gallop(array, 0, array.Length, 9, out index));
            Assert.Equal(array.Length - 1, index);
            Assert.False(SortedOffsets.Gallop(array, 0, array.Length, 6, out index));
            Assert.Equal(array.Length - 1, index);
        }

        [Fact]
        public void EveryIntersectionStrategyComputesMultisetIntersection()
        {
            var random = new Random(99);
            for (var trial = 0; trial < 400; trial++)
            {
                var universe = random.Next(1, 500);
                var duplicates = trial % 2 == 0;
                var a = RandomSorted(random, random.Next(0, 200), universe, duplicates);
                var b = RandomSorted(random, random.Next(0, 200), universe, duplicates);
                var expected = MultisetIntersect(a, b);
                foreach (var kernel in kernels)
                {
                    var cur = Embed(random, a, out var curStart);
                    var other = Embed(random, b, out var otherStart);
                    var otherCopy = (int[])other.Clone();
                    var end = kernel(cur, curStart, curStart + a.Count, other, otherStart, otherStart + b.Count);
                    Assert.Equal(expected, cur[curStart..end]);
                    Assert.Equal(otherCopy, other);
                }
            }
        }

        [Fact]
        public void StrategiesAgreeOnSkewedSizes()
        {
            var random = new Random(5);
            foreach (var (curCount, otherCount) in new[] { (5, 100), (100, 5), (16, 16), (17, 4), (4, 17), (64, 16), (65, 16), (16, 64), (16, 65), (1, 10000), (10000, 1), (0, 50), (50, 0) })
            {
                var a = RandomSorted(random, curCount, 20000, duplicates: false);
                var b = RandomSorted(random, otherCount, 20000, duplicates: false);
                var b2 = b.Concat(a.Where((_, i) => i % 2 == 0)).Distinct().OrderBy(x => x).ToList();
                var expected = a.Intersect(b2).OrderBy(x => x).ToList();
                foreach (var kernel in kernels)
                {
                    var cur = a.ToArray();
                    var end = kernel(cur, 0, cur.Length, b2.ToArray(), 0, b2.Count);
                    Assert.Equal(expected, cur[..end]);
                }
            }
        }

        [Fact]
        public void RetainCompactsAndCallsFilterOncePerRow()
        {
            var random = new Random(3);
            for (var trial = 0; trial < 200; trial++)
            {
                var rows = RandomSorted(random, random.Next(0, 100), 200, duplicates: true);
                var array = Embed(random, rows, out var start);
                var modulus = random.Next(1, 5);
                var filter = new PredicateFilter(r => r % modulus == 0);
                var end = SortedOffsets.Retain(array, start, start + rows.Count, ref filter);
                Assert.Equal(rows, filter.Seen);
                Assert.Equal(rows.Where(r => r % modulus == 0), array[start..end]);
            }
        }

        [Fact]
        public void KernelsRejectRunsOutsideTheArray()
        {
            var a = new[] { 1, 2, 3 };
            Assert.Throws<ArgumentOutOfRangeException>(() => SortedOffsets.IntersectByMerge(a, 0, 4, a, 0, 3));
            Assert.Throws<ArgumentOutOfRangeException>(() => SortedOffsets.IntersectByGallopInCur(a, 0, 3, a, 2, 1));
            Assert.Throws<ArgumentOutOfRangeException>(() => SortedOffsets.IntersectByGallopInOther(a, -1, 3, a, 0, 3));
            Assert.Throws<ArgumentOutOfRangeException>(() => SortedOffsets.Gallop(a, 0, 4, 1, out _));
            Assert.Throws<ArgumentOutOfRangeException>(() => SortedOffsets.Gallop(a, -1, 3, 1, out _));
            var filter = new PredicateFilter(_ => true);
            Assert.Throws<ArgumentOutOfRangeException>(() => SortedOffsets.Retain(a, 1, 4, ref filter));
        }

        [Fact]
        public void IsSortedDetectsInversions()
        {
            Assert.True(SortedOffsets.IsSorted([1, 1, 2], 0, 3));
            Assert.False(SortedOffsets.IsSorted([1, 3, 2], 0, 3));
            Assert.True(SortedOffsets.IsSorted([5, 3, 4], 1, 3));
        }

    }

}

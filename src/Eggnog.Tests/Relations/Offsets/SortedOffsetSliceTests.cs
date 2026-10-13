using System;
using System.Collections.Generic;
using System.Linq;

using Xunit;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// Tests for <see cref="SortedOffsetSlice"/>.
    /// </summary>
    public class SortedOffsetSliceTests
    {

        [Fact]
        public void PositionsAreRelativeToStart()
        {
            var slice = new SortedOffsetSlice(new[] { 0, 3, 5, 5, 8, 100 }, 1, 5);
            Assert.Equal(4, slice.Length);
            Assert.Equal(3, slice[0]);
            Assert.Equal(8, slice[3]);
            Assert.Throws<ArgumentOutOfRangeException>(() => slice[4]);
            Assert.Equal([3, 5, 5, 8], slice.AsSpan().ToArray());
            Assert.Equal("[3, 5, 5, 8]", slice.ToString());
        }

        [Fact]
        public void BinarySearchByIdIsLowerBound()
        {
            var slice = new SortedOffsetSlice(new[] { -1, 3, 5, 5, 5, 8, 100 }, 1, 6);
            Assert.Equal(0, slice.BinarySearchById(0));
            Assert.Equal(0, slice.BinarySearchById(3));
            Assert.Equal(1, slice.BinarySearchById(4));
            Assert.Equal(1, slice.BinarySearchById(5));
            Assert.Equal(4, slice.BinarySearchById(6));
            Assert.Equal(4, slice.BinarySearchById(8));
            Assert.Equal(5, slice.BinarySearchById(9));
            Assert.Equal(0, SortedOffsetSlice.Empty.BinarySearchById(9));
        }

        [Fact]
        public void ScanForOffsetFindsFirstCopyAtOrAfterCursor()
        {
            var slice = new SortedOffsetSlice(new[] { 1, 2, 2, 2, 7, 9, 9, 15 }, 0, 8);
            Assert.True(slice.ScanForOffset(0, 2, out var i));
            Assert.Equal(1, i);
            Assert.True(slice.ScanForOffset(2, 2, out i));
            Assert.Equal(2, i);
            Assert.False(slice.ScanForOffset(4, 2, out i));
            Assert.Equal(4, i);
            Assert.False(slice.ScanForOffset(0, 8, out i));
            Assert.Equal(5, i);
            Assert.True(slice.ScanForOffset(0, 15, out i));
            Assert.Equal(7, i);
            Assert.False(slice.ScanForOffset(0, 16, out i));
            Assert.Equal(8, i);
            Assert.False(slice.ScanForOffset(8, 1, out i));
            Assert.Equal(8, i);
            Assert.False(slice.ScanForOffset(100, 1, out i));
            Assert.Equal(8, i);
        }

        [Fact]
        public void ScanForOffsetMatchesReferenceOnRandomSlices()
        {
            var random = new Random(1234);
            for (var trial = 0; trial < 300; trial++)
            {
                var rows = OffsetTestUtil.RandomSorted(random, random.Next(0, 200), random.Next(1, 400), duplicates: trial % 2 == 0);
                var pad = random.Next(0, 4);
                var array = Enumerable.Repeat(-5, pad).Concat(rows).Concat(Enumerable.Repeat(int.MaxValue, pad)).ToArray();
                var slice = new SortedOffsetSlice(array, pad, pad + rows.Count);
                for (var probe = 0; probe < 20; probe++)
                {
                    var from = random.Next(0, rows.Count + 2);
                    var target = random.Next(-2, 410);
                    var expected = from;
                    while (expected < rows.Count && rows[expected] < target)
                        expected++;

                    var found = slice.ScanForOffset(from, target, out var index);
                    Assert.Equal(Math.Min(expected, rows.Count), index);
                    Assert.Equal(expected < rows.Count && rows[expected] == target, found);
                }
            }
        }

        [Fact]
        public void ScanForOffsetRejectsNegativeCursor()
        {
            // The slice sits inside a larger array; a negative cursor must not reach the rows before Start.
            var slice = new SortedOffsetSlice(new[] { 1, 2, 10, 20, 30 }, 2, 5);
            Assert.Throws<ArgumentOutOfRangeException>("from", () => slice.ScanForOffset(-1, 2, out _));
            Assert.Throws<ArgumentOutOfRangeException>("from", () => slice.ScanForOffset(int.MinValue, 10, out _));
            Assert.Throws<ArgumentOutOfRangeException>("from", () => SortedOffsetSlice.Empty.ScanForOffset(-1, 0, out _));
        }

        [Fact]
        public void SubsliceIsRelativeAndChecked()
        {
            var slice = new SortedOffsetSlice(new[] { 0, 1, 2, 3, 4, 5 }, 1, 6);
            var sub = slice.Subslice(1, 3);
            Assert.Equal([2, 3], sub.AsSpan().ToArray());
            Assert.Equal(2, sub.Start);
            Assert.Equal(4, sub.End);
            Assert.True(slice.Subslice(5, 5).IsEmpty);
            Assert.Throws<ArgumentOutOfRangeException>(() => slice.Subslice(2, 6));
            Assert.Throws<ArgumentOutOfRangeException>(() => slice.Subslice(3, 2));
            Assert.True(default(SortedOffsetSlice).Subslice(0, 0).IsEmpty);
        }

        [Fact]
        public void BoundsAndIteration()
        {
            var slice = new SortedOffsetSlice(new[] { 4, 4, 10 }, 0, 3);
            Assert.True(slice.TryGetBounds(out var bounds));
            Assert.Equal(new OffsetRange(4, 11), bounds);
            Assert.False(SortedOffsetSlice.Empty.TryGetBounds(out _));
            Assert.True(SortedOffsetSlice.Empty.IsEmpty);
            Assert.False(default(SortedOffsetSlice).TryGetBounds(out _));

            var visitor = new CollectingVisitor();
            slice.ForEach(ref visitor);
            Assert.Equal([4, 4, 10], visitor.Rows);

            var enumerated = new List<int>();
            foreach (var row in slice)
                enumerated.Add(row);

            Assert.Equal(visitor.Rows, enumerated);

            var none = new List<int>();
            foreach (var row in default(SortedOffsetSlice))
                none.Add(row);

            Assert.Empty(none);
        }

        [Fact]
        public void ConstructorChecksBounds()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new SortedOffsetSlice(new[] { 1, 2 }, 1, 3));
            Assert.Throws<ArgumentOutOfRangeException>(() => new SortedOffsetSlice(new[] { 1, 2 }, 2, 1));
        }

#if DEBUG

        [Fact]
        public void ConstructorAssertsSortednessInDebug()
        {
            // Port of SortedOffsetSlice::new_unchecked's debug_assert.
            using var scope = new DebugAssertScope();
            Assert.Throws<DebugAssertFailedException>(() => new SortedOffsetSlice(new[] { 1, 3, 2 }, 0, 3));
        }

#endif

        [Fact]
        public void ScanForOffsetNeverReachesOutsideTheSlice()
        {
            // egglog's scan_for_offset takes a usize cursor, so it can never look before the slice. Here the cursor is
            // an int relative to Start, and ScanForOffset rejects a negative one: forwarded as Start + from, it would
            // search the owner's array before the slice.
            var array = new[] { 1, 2, 10, 20, 30 };
            var slice = new SortedOffsetSlice(array, 2, 5);

            var threw = false;
            var found = false;
            var index = 0;
            try
            {
                found = slice.ScanForOffset(-2, 1, out index);
            }
            catch (ArgumentOutOfRangeException)
            {
                threw = true;
            }

            Assert.True(threw || (found == false && index >= 0 && index <= slice.Length),
                $"row 1 is not in the slice {slice}, yet ScanForOffset(-2, 1) returned found={found}, index={index}");
        }

    }

}

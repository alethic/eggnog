using System;
using System.Collections.Generic;
using System.Linq;

using Xunit;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// Tests for <see cref="SortedOffsetVector"/>.
    /// </summary>
    public class SortedOffsetVectorTests
    {

        static List<int> Contents(SortedOffsetVector v)
        {
            var rows = new List<int>();
            foreach (var row in v)
                rows.Add(row);

            var visitor = new CollectingVisitor();
            v.ForEach(ref visitor);
            Assert.Equal(rows, visitor.Rows);
            Assert.Equal(rows.Count, v.Count);
            return rows;
        }

        [Fact]
        public void DefaultIsEmptyAndOwnsNothing()
        {
            var v = new SortedOffsetVector();
            Assert.True(v.IsEmpty);
            Assert.Empty(Contents(v));
            Assert.False(v.TryGetBounds(out _));
            Assert.True(v.Slice().IsEmpty);
            v.Dispose();
        }

        [Fact]
        public void PushAcceptsEqualAndLargerRows()
        {
            var v = new SortedOffsetVector();
            for (var i = 0; i < 1000; i++)
            {
                v.Push(i / 3);
                Assert.Equal(i / 3, v[i]);
            }

            Assert.Equal(Enumerable.Range(0, 1000).Select(i => i / 3), Contents(v));
            Assert.True(v.TryGetBounds(out var bounds));
            Assert.Equal(new OffsetRange(0, 334), bounds);
            v.Dispose();
            Assert.True(v.IsEmpty);
        }

        [Fact]
        public void PushUncheckedAppends()
        {
            var v = new SortedOffsetVector();
            v.PushUnchecked(2);
            v.PushUnchecked(5);
            Assert.Equal([2, 5], Contents(v));
            v.Dispose();
        }

        [Fact]
        public void ExtendNonoverlappingAppendsOrThrows()
        {
            var v = new SortedOffsetVector();
            v.ExtendNonoverlapping(SortedOffsetSlice.Empty);
            Assert.True(v.IsEmpty);

            v.ExtendNonoverlapping(new SortedOffsetSlice(new[] { 9, 1, 2, 3 }, 1, 4));
            Assert.Equal([1, 2, 3], Contents(v));

            v.ExtendNonoverlapping(new SortedOffsetSlice(new[] { 3, 7 }, 0, 2));
            Assert.Equal([1, 2, 3, 3, 7], Contents(v));

            var threw = false;
            try
            {
                v.ExtendNonoverlapping(new SortedOffsetSlice(new[] { 6, 8 }, 0, 2));
            }
            catch (InvalidOperationException)
            {
                threw = true;
            }

            Assert.True(threw);
            Assert.Equal([1, 2, 3, 3, 7], Contents(v));

            var big = Enumerable.Range(10, 500).ToArray();
            v.ExtendNonoverlapping(new SortedOffsetSlice(big, 0, big.Length));
            Assert.Equal(new[] { 1, 2, 3, 3, 7 }.Concat(big), Contents(v));
            v.Dispose();
        }

        [Fact]
        public void FillFromDenseReplacesContents()
        {
            var v = new SortedOffsetVector();
            v.Push(100);
            v.FillFromDense(new OffsetRange(3, 8));
            Assert.Equal([3, 4, 5, 6, 7], Contents(v));
            v.FillFromDense(new OffsetRange(4, 4));
            Assert.True(v.IsEmpty);
            v.FillFromDense(new OffsetRange(0, 300));
            Assert.Equal(Enumerable.Range(0, 300), Contents(v));
            v.Dispose();
        }

        [Fact]
        public void RetainKeepsOrder()
        {
            var v = new SortedOffsetVector();
            v.FillFromDense(new OffsetRange(0, 50));
            var filter = new PredicateFilter(r => r % 7 == 3);
            v.Retain(ref filter);
            Assert.Equal(Enumerable.Range(0, 50), filter.Seen);
            Assert.Equal(Enumerable.Range(0, 50).Where(r => r % 7 == 3), Contents(v));
            v.Dispose();
        }

        [Fact]
        public void ClearKeepsBufferForReuse()
        {
            var v = new SortedOffsetVector();
            v.FillFromDense(new OffsetRange(0, 10));
            v.Clear();
            Assert.True(v.IsEmpty);
            v.Push(4);
            Assert.Equal([4], Contents(v));
            v.Dispose();
        }

        [Fact]
        public void ToSubsetMovesBufferIntoSparseSubset()
        {
            var v = new SortedOffsetVector();
            v.Push(1);
            v.Push(4);
            v.Push(4);
            var s = v.ToSubset();
            Assert.True(v.IsEmpty);
            Assert.False(s.IsDense);
            Assert.Equal([1, 4, 4], OffsetTestUtil.Collect(s));
            s.Dispose();

            var empty = new SortedOffsetVector().ToSubset();
            Assert.False(empty.IsDense);
            Assert.True(empty.IsEmpty);
            empty.Dispose();
        }

        [Fact]
        public void ToStringListsRows()
        {
            var v = new SortedOffsetVector();
            v.Push(2);
            v.Push(3);
            Assert.Equal("[2, 3]", v.ToString());
            v.Dispose();
        }

    }

}

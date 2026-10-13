using System;
using System.Linq;

using Xunit;

using static Eggnog.Relations.Tests.OffsetTestUtil;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// Tests for <see cref="SubsetRef"/>.
    /// </summary>
    public class SubsetRefTests
    {

        [Fact]
        public void DefaultIsEmptyDenseRange()
        {
            SubsetRef s = default;
            Assert.True(s.IsDense);
            Assert.True(s.IsEmpty);
            Assert.Equal(0, s.Size);
            Assert.Empty(Collect(s));
        }

        [Fact]
        public void DenseAndSparseReportKindSizeAndRows()
        {
            var dense = SubsetRef.Dense(new OffsetRange(3, 7));
            Assert.True(dense.IsDense);
            Assert.Equal(4, dense.Size);
            Assert.Equal([3, 4, 5, 6], Collect(dense));
            Assert.True(dense.TryGetDense(out var range));
            Assert.Equal(new OffsetRange(3, 7), range);

            var array = new[] { 99, 2, 4, 4, 8, 99 };
            var sparse = SubsetRef.Sparse(array, 1, 5);
            Assert.False(sparse.IsDense);
            Assert.False(sparse.TryGetDense(out _));
            Assert.Equal(4, sparse.Size);
            Assert.Equal([2, 4, 4, 8], Collect(sparse));
            Assert.Equal("Sparse[2, 4, 4, 8]", sparse.ToString());
            Assert.Equal("Dense[3..7)", dense.ToString());
        }

        [Fact]
        public void SparseRejectsRunOutsideArray()
        {
            var array = new[] { 1, 2, 3 };
            Assert.Throws<ArgumentOutOfRangeException>(() => SubsetRef.Sparse(array, 2, 4));
            Assert.Throws<ArgumentOutOfRangeException>(() => SubsetRef.Sparse(array, 2, 1));
            Assert.Throws<ArgumentOutOfRangeException>(() => SubsetRef.Sparse(array, -1, 1));
        }

#if DEBUG

        [Fact]
        public void SparseAssertsSortednessInDebug()
        {
            using var scope = new DebugAssertScope();
            Assert.Throws<DebugAssertFailedException>(() => SubsetRef.Sparse(new[] { 3, 1 }, 0, 2));
        }

#endif

        [Fact]
        public void FromSortedRunTurnsSingletonDense()
        {
            var ids = new[] { 4, 9, 9, 12 };
            var one = SubsetRef.FromSortedRun(ids, 1, 2);
            Assert.True(one.TryGetDense(out var range));
            Assert.Equal(new OffsetRange(9, 10), range);

            var two = SubsetRef.FromSortedRun(ids, 1, 3);
            Assert.False(two.IsDense);
            Assert.Equal([9, 9], Collect(two));

            var none = SubsetRef.FromSortedRun(ids, 2, 2);
            Assert.False(none.IsDense);
            Assert.True(none.IsEmpty);
        }

        [Fact]
        public void AsSliceThrowsOnDense()
        {
            Assert.Throws<InvalidOperationException>(() => SubsetRef.Dense(new OffsetRange(0, 3)).AsSlice());
            var slice = SubsetRef.Sparse(new[] { 0, 5, 6 }, 1, 3).AsSlice();
            Assert.Equal(2, slice.Length);
            Assert.Equal(5, slice[0]);
        }

        [Fact]
        public void ToOwnedCopiesSparseAndKeepsDense()
        {
            var dense = SubsetRef.Dense(new OffsetRange(2, 5)).ToOwned();
            Assert.Equal(Subset.Dense(2, 5), dense);

            var array = new[] { 1, 3, 5 };
            var owned = SubsetRef.Sparse(array, 0, 3).ToOwned();
            array[1] = 4;
            Assert.Equal([1, 3, 5], Collect(owned));
            owned.Dispose();

            var empty = SubsetRef.Sparse(Array.Empty<int>(), 0, 0).ToOwned();
            Assert.False(empty.IsDense);
            Assert.True(empty.IsEmpty);
            empty.Dispose();
        }

        [Fact]
        public void IterBoundedDenseFollowsEgglog()
        {
            var s = SubsetRef.Dense(new OffsetRange(10, 15));

            var v = new CollectingVisitor();
            Assert.Equal(3, s.IterBounded(0, 3, ref v));
            Assert.Equal([10, 11, 12], v.Rows);

            v = new CollectingVisitor();
            Assert.Equal(-1, s.IterBounded(3, 100, ref v));
            Assert.Equal([13, 14], v.Rows);

            v = new CollectingVisitor();
            Assert.Equal(-1, s.IterBounded(3, 5, ref v));
            Assert.Equal([13, 14], v.Rows);

            // Starting at or past the end visits nothing and reports the end.
            v = new CollectingVisitor();
            Assert.Equal(-1, s.IterBounded(5, 9, ref v));
            Assert.Equal(-1, s.IterBounded(7, 9, ref v));
            Assert.Empty(v.Rows);

            // An empty chunk before the end reports its start again, as egglog does.
            Assert.Equal(2, s.IterBounded(2, 2, ref v));
            Assert.Equal(2, s.IterBounded(2, 1, ref v));
            Assert.Empty(v.Rows);

            // Upper bounds near int.MaxValue do not overflow (egglog uses saturating_add).
            v = new CollectingVisitor();
            Assert.Equal(-1, s.IterBounded(1, int.MaxValue, ref v));
            Assert.Equal([11, 12, 13, 14], v.Rows);
        }

        [Fact]
        public void IterBoundedSparseFollowsEgglog()
        {
            var s = SubsetRef.Sparse(new[] { 2, 4, 6, 8, 10 }, 0, 5);

            var v = new CollectingVisitor();
            Assert.Equal(2, s.IterBounded(0, 2, ref v));
            Assert.Equal(-1, s.IterBounded(2, 5, ref v));
            Assert.Equal([2, 4, 6, 8, 10], v.Rows);

            v = new CollectingVisitor();
            Assert.Equal(-1, s.IterBounded(4, int.MaxValue, ref v));
            Assert.Equal([10], v.Rows);

            v = new CollectingVisitor();
            Assert.Equal(3, s.IterBounded(3, 3, ref v));
            Assert.Equal(-1, s.IterBounded(5, 5, ref v));
            Assert.Empty(v.Rows);

            // egglog panics on a chunk that starts past its clipped end.
            var threw = false;
            try
            {
                s.IterBounded(4, 3, ref v);
            }
            catch (ArgumentOutOfRangeException)
            {
                threw = true;
            }

            Assert.True(threw);
        }

        [Fact]
        public void IterBoundedOnOffsetSparseUsesPositions()
        {
            var owner = OffsetSparse([5, 7, 9, 11]);
            var s = owner.AsRef();
            var v = new CollectingVisitor();
            Assert.Equal(3, s.IterBounded(1, 3, ref v));
            Assert.Equal([7, 9], v.Rows);
            owner.Dispose();
        }

        [Fact]
        public void IterBoundedRejectsNegativePositionsWithoutVisiting()
        {
            // egglog's positions are usize and its None never comes back as a start; the port's -1 must not either.
            var dense = SubsetRef.Dense(new OffsetRange(10, 15));
            var sparse = SubsetRef.Sparse(new[] { 100, 5, 7, 9 }, 1, 4);
            foreach (var s in new[] { dense, sparse })
            {
                var v = new CollectingVisitor();
                Assert.Throws<ArgumentOutOfRangeException>("from", () => s.IterBounded(-1, 3, ref v));
                Assert.Throws<ArgumentOutOfRangeException>("from", () => s.IterBounded(int.MinValue, 0, ref v));
                Assert.Throws<ArgumentOutOfRangeException>("to", () => s.IterBounded(0, -1, ref v));
                Assert.Empty(v.Rows);
            }
        }

        [Fact]
        public void TryIntersectDenseClampsWithoutAllocating()
        {
            var dense = SubsetRef.Dense(new OffsetRange(10, 20));
            Assert.True(dense.TryIntersectDense(new OffsetRange(15, 30), out var clamped));
            Assert.True(clamped.TryGetDense(out var range));
            Assert.Equal(new OffsetRange(15, 20), range);
            Assert.False(dense.TryIntersectDense(new OffsetRange(20, 30), out _));
            Assert.False(dense.TryIntersectDense(new OffsetRange(12, 12), out _));

            var array = new[] { 1, 4, 4, 7, 12 };
            var sparse = SubsetRef.Sparse(array, 0, array.Length);
            Assert.True(sparse.TryIntersectDense(new OffsetRange(4, 8), out var cut));
            Assert.False(cut.IsDense);
            Assert.Same(array, cut.Rows);
            Assert.Equal([4, 4, 7], Collect(cut));
            Assert.False(sparse.TryIntersectDense(new OffsetRange(8, 12), out _));
            Assert.False(SubsetRef.Sparse(array, 2, 2).TryIntersectDense(new OffsetRange(0, 100), out _));
        }

        [Fact]
        public void BoundsOfSparseSpanFirstToLast()
        {
            var s = SubsetRef.Sparse(new[] { 3, 3, 9 }, 0, 3);
            Assert.True(s.TryGetBounds(out var bounds));
            Assert.Equal(new OffsetRange(3, 10), bounds);
            Assert.False(SubsetRef.Sparse(new[] { 3 }, 1, 1).TryGetBounds(out _));
            Assert.True(SubsetRef.Dense(new OffsetRange(6, 6)).TryGetBounds(out var empty));
            Assert.Equal(new OffsetRange(6, 6), empty);
        }

        [Fact]
        public void EnumeratorMatchesForEach()
        {
            var s = SubsetRef.Sparse(Enumerable.Range(0, 50).Select(x => x * 3).ToArray(), 5, 40);
            Assert.Equal(Enumerable.Range(5, 35).Select(x => x * 3), Collect(s));
        }

    }

}

using System;
using System.Collections.Generic;
using System.Linq;

using Xunit;

using static Eggnog.Relations.Tests.OffsetTestUtil;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// Tests for <see cref="Subset"/>: the ports of egglog's core-relations/src/offsets/tests.rs first, then the
    /// behaviors of each arm that those tests do not reach.
    /// </summary>
    public class SubsetTests
    {

        // ---- Ported from core-relations/src/offsets/tests.rs ----

        [Fact]
        public void SubsetPush()
        {
            var elts = Enumerable.Range(0, 10).ToArray();
            var s = Subset.Dense(new OffsetRange(1, 2));
            Assert.Equal([1], Collect(s).Select(o => elts[o]));
            s.AddRowSorted(2);
            s.AddRowSorted(3);
            Assert.Equal([1, 2, 3], Collect(s).Select(o => elts[o]));
            s.AddRowSorted(7);
            Assert.Equal([1, 2, 3, 7], Collect(s).Select(o => elts[o]));
            s.Dispose();
        }

#if DEBUG

        [Fact]
        public void BadOffsetRange()
        {
            using var scope = new DebugAssertScope();
            Assert.Throws<DebugAssertFailedException>(() => new OffsetRange(3, 2));
        }

        [Fact]
        public void BadOffsetStride()
        {
            // egglog's bad_offset_stride is a copy of bad_offset_range.
            using var scope = new DebugAssertScope();
            Assert.Throws<DebugAssertFailedException>(() => new OffsetRange(3, 2));
        }

#endif

        [Fact]
        public void NotSortedVecPush()
        {
            // egglog checks the order with assert!, so this holds in release builds too.
            var v = new SortedOffsetVector();
            v.Push(3);
            var threw = false;
            try
            {
                v.Push(1);
            }
            catch (InvalidOperationException)
            {
                threw = true;
            }

            Assert.True(threw);
            v.Dispose();
        }

        [Fact]
        public void Intersect()
        {
            var elts = new List<List<int>>
            {
                new() { 1, 3, 4, 8, 9, 12, 20 },
                new() { 3, 4, 8, 9, 11, 12, 15, 18, 20 },
                new() { 3, 4, 8, 9, 11, 12, 15, 18, 20, 22, 24 },
                new() { 1, 4, 8, 9, 11, 12, 15, 18, 20 },
                new(),
                Enumerable.Range(0, 20).ToList(),
                Enumerable.Range(2, 98).ToList(),
                Enumerable.Range(4, 46).ToList(),
            };

            // Size-skewed pairs that exercise the two-pointer/galloping boundary: two-pointer when max/min < 4,
            // galloping otherwise.
            var skewed = new List<List<int>>
            {
                Enumerable.Range(0, 5).ToList(),
                Enumerable.Range(0, 100).ToList(),
                Enumerable.Range(0, 100).Where(x => x % 7 == 0).ToList(),
            };

            var all = elts.Concat(skewed).ToList();
            foreach (var l in all)
            {
                foreach (var r in all)
                {
                    var lSub = Build(l);
                    var rSub = Build(r);
                    var expected = l.Intersect(r).OrderBy(x => x).ToList();
                    lSub.Intersect(rSub.AsRef());
                    Assert.True(expected.SequenceEqual(Collect(lSub)), $"l: [{string.Join(", ", l)}], r: [{string.Join(", ", r)}]");
                    lSub.Dispose();
                    rSub.Dispose();
                }
            }
        }

        [Fact]
        public void IterBounded()
        {
            var s1 = Subset.Empty;
            var never = new CollectingVisitor();
            Assert.Equal(-1, s1.AsRef().IterBounded(0, 100, ref never));
            Assert.Empty(never.Rows);

            var s2 = Subset.Empty;
            for (var i = 0; i < 100; i++)
            {
                s1.AddRowSorted(i);
                s2.AddRowSorted(i * 2);
            }

            Assert.True(s1.IsDense);
            Assert.False(s2.IsDense);

            var got = new CollectingVisitor();
            Assert.Equal(12, s1.AsRef().IterBounded(2, 12, ref got));
            Assert.Equal(Enumerable.Range(2, 10), got.Rows);

            got = new CollectingVisitor();
            Assert.Equal(12, s2.AsRef().IterBounded(2, 12, ref got));
            Assert.Equal(Enumerable.Range(2, 10).Select(x => x * 2), got.Rows);

            s2.Dispose();
        }

        // ---- AddRowSorted ----

        [Fact]
        public void AddRowSortedOnEmptyDenseStartsAnywhere()
        {
            // egglog resets an empty range to [row, row + 1) wherever it was.
            var s = Subset.Dense(5, 5);
            s.AddRowSorted(3);
            Assert.True(s.TryGetDense(out var range));
            Assert.Equal(new OffsetRange(3, 4), range);
        }

        [Fact]
        public void AddRowSortedExtendsDenseThenPromotesOnGap()
        {
            var s = Subset.Empty;
            s.AddRowSorted(4);
            s.AddRowSorted(5);
            s.AddRowSorted(6);
            Assert.True(s.TryGetDense(out var range));
            Assert.Equal(new OffsetRange(4, 7), range);

            s.AddRowSorted(9);
            Assert.False(s.IsDense);
            Assert.Equal([4, 5, 6, 9], Collect(s));
            s.Dispose();
        }

        [Fact]
        public void AddRowSortedRepeatingLastRowPromotesWithDuplicate()
        {
            var s = Subset.Dense(2, 4);
            s.AddRowSorted(3);
            Assert.False(s.IsDense);
            Assert.Equal([2, 3, 3], Collect(s));
            s.AddRowSorted(3);
            Assert.Equal([2, 3, 3, 3], Collect(s));
            s.Dispose();
        }

        [Fact]
        public void AddRowSortedOutOfOrderThrowsAndLeavesSubsetUnchanged()
        {
            var dense = Subset.Dense(2, 6);
            var threw = false;
            try
            {
                dense.AddRowSorted(3);
            }
            catch (InvalidOperationException)
            {
                threw = true;
            }

            Assert.True(threw);
            Assert.Equal(Subset.Dense(2, 6), dense);

            var sparse = Build([1, 5, 9]);
            threw = false;
            try
            {
                sparse.AddRowSorted(8);
            }
            catch (InvalidOperationException)
            {
                threw = true;
            }

            Assert.True(threw);
            Assert.Equal([1, 5, 9], Collect(sparse));
            sparse.Dispose();
        }

        [Fact]
        public void AddRowSortedGrowsSparseBuffer()
        {
            var s = Subset.Empty;
            var expected = new List<int>();
            for (var i = 0; i < 5000; i++)
            {
                s.AddRowSorted(i * 3);
                expected.Add(i * 3);
            }

            Assert.Equal(expected, Collect(s));
            s.Dispose();
        }

        [Fact]
        public void AddRowSortedAfterNarrowingReusesFreedFront()
        {
            var s = Sparse(Enumerable.Range(0, 16).Select(x => x * 2));
            s.Intersect(SubsetRef.Dense(new OffsetRange(20, 100)));
            Assert.Equal([20, 22, 24, 26, 28, 30], Collect(s));
            for (var row = 31; row < 60; row++)
                s.AddRowSorted(row);

            Assert.Equal(new[] { 20, 22, 24, 26, 28, 30 }.Concat(Enumerable.Range(31, 29)), Collect(s));
            s.Dispose();
        }

        // ---- Intersect, arm by arm ----

        [Fact]
        public void IntersectDenseDenseLeavesEmptyRangeAtLowerBound()
        {
            var s = Subset.Dense(10, 20);
            s.Intersect(SubsetRef.Dense(new OffsetRange(25, 30)));
            Assert.True(s.IsEmpty);
            Assert.True(s.TryGetDense(out var range));
            Assert.Equal(new OffsetRange(25, 25), range);
            Assert.NotEqual(Subset.Empty, s);

            var t = Subset.Dense(10, 20);
            t.Intersect(SubsetRef.Dense(new OffsetRange(15, 30)));
            Assert.Equal(Subset.Dense(15, 20), t);
        }

        [Fact]
        public void IntersectDenseSparseCopiesClippedRun()
        {
            var other = Sparse([1, 4, 4, 7, 12]);
            var s = Subset.Dense(3, 10);
            s.Intersect(other.AsRef());
            Assert.False(s.IsDense);
            Assert.Equal([4, 4, 7], Collect(s));
            s.Dispose();

            var none = Subset.Dense(8, 12);
            none.Intersect(other.AsRef());
            Assert.Equal(Subset.Empty, none);

            var empty = Sparse([]);
            var d = Subset.Dense(3, 10);
            d.Intersect(empty.AsRef());
            Assert.Equal(Subset.Empty, d);

            other.Dispose();
            empty.Dispose();
        }

        [Fact]
        public void IntersectSparseDenseNarrowsInPlaceAndStaysSparse()
        {
            var s = Sparse([1, 4, 4, 7, 12]);
            s.Intersect(SubsetRef.Dense(new OffsetRange(4, 8)));
            Assert.False(s.IsDense);
            Assert.Equal([4, 4, 7], Collect(s));

            s.Intersect(SubsetRef.Dense(new OffsetRange(5, 5)));
            Assert.False(s.IsDense);
            Assert.True(s.IsEmpty);
            Assert.False(s.TryGetBounds(out _));
            s.Dispose();
        }

        [Fact]
        public void IntersectSparseSparseUsesMultisetSemantics()
        {
            var s = Sparse([1, 3, 3, 3, 5, 8]);
            var o = Sparse([3, 3, 5, 9]);
            s.Intersect(o.AsRef());
            Assert.Equal([3, 3, 5], Collect(s));

            var none = Sparse([100]);
            s.Intersect(none.AsRef());
            Assert.False(s.IsDense);
            Assert.True(s.IsEmpty);

            s.Dispose();
            o.Dispose();
            none.Dispose();
        }

        [Fact]
        public void IntersectWithItselfIsIdentity()
        {
            var s = Sparse([1, 2, 2, 9]);
            s.Intersect(s.AsRef());
            Assert.Equal([1, 2, 2, 9], Collect(s));
            s.Dispose();
        }

        // ---- Retain ----

        [Fact]
        public void RetainDenseStaysDenseWhileContiguous()
        {
            var s = Subset.Dense(0, 20);
            var filter = new PredicateFilter(r => r >= 5 && r < 9);
            s.Retain(ref filter);
            Assert.Equal(Enumerable.Range(0, 20), filter.Seen);
            Assert.True(s.TryGetDense(out var range));
            Assert.Equal(new OffsetRange(5, 9), range);
        }

        [Fact]
        public void RetainDenseKeepingNothingIsEmpty()
        {
            var s = Subset.Dense(7, 20);
            var filter = new PredicateFilter(_ => false);
            s.Retain(ref filter);
            Assert.Equal(Subset.Empty, s);
        }

        [Fact]
        public void RetainDensePromotesAtFirstGap()
        {
            var s = Subset.Dense(0, 1000);
            var filter = new PredicateFilter(r => r % 3 != 1);
            s.Retain(ref filter);
            Assert.False(s.IsDense);
            Assert.Equal(Enumerable.Range(0, 1000).Where(r => r % 3 != 1), Collect(s));
            Assert.Equal(Enumerable.Range(0, 1000), filter.Seen);
            s.Dispose();
        }

        [Fact]
        public void RetainSparseCompactsInPlaceAndStaysSparse()
        {
            var s = OffsetSparse([3, 4, 4, 9, 10, 15]);
            var filter = new PredicateFilter(r => r % 2 == 0);
            s.Retain(ref filter);
            Assert.Equal([3, 4, 4, 9, 10, 15], filter.Seen);
            Assert.Equal([4, 4, 10], Collect(s));

            var none = new PredicateFilter(_ => false);
            s.Retain(ref none);
            Assert.False(s.IsDense);
            Assert.True(s.IsEmpty);
            s.Dispose();
        }

        // ---- Ownership, equality, conversions ----

        [Fact]
        public void CloneIsIndependent()
        {
            var s = Sparse([1, 5, 9]);
            var c = s.Clone();
            Assert.Equal(s, c);
            c.AddRowSorted(10);
            Assert.Equal([1, 5, 9], Collect(s));
            Assert.Equal([1, 5, 9, 10], Collect(c));
            s.Dispose();
            c.Dispose();

            var d = Subset.Dense(3, 8);
            Assert.Equal(d, d.Clone());
        }

        [Fact]
        public void TakeMovesAndLeavesEmpty()
        {
            var s = Sparse([1, 5, 9]);
            var t = s.Take();
            Assert.Equal(Subset.Empty, s);
            Assert.Equal([1, 5, 9], Collect(t));
            t.Dispose();
        }

        [Fact]
        public void DisposeResetsToEmptyAndIsIdempotent()
        {
            var s = Sparse([1, 5, 9]);
            s.Dispose();
            Assert.Equal(Subset.Empty, s);
            s.Dispose();
            Assert.Equal(Subset.Empty, s);

            var d = Subset.Dense(2, 3);
            d.Dispose();
            Assert.Equal(Subset.Empty, d);
        }

        [Fact]
        public void EqualityIsStructural()
        {
            var dense = Subset.Dense(0, 3);
            var sparse = Sparse([0, 1, 2]);
            var sparse2 = Sparse(new List<int> { 0, 1, 2 });
            Assert.NotEqual(dense, sparse);
            Assert.Equal(sparse, sparse2);
            Assert.Equal(sparse.GetHashCode(), sparse2.GetHashCode());
            Assert.True(sparse == sparse2);
            Assert.False(dense == sparse);
            Assert.True(dense != sparse);
            Assert.NotEqual(Subset.Dense(3, 3), Subset.Dense(0, 0));

            var shifted = OffsetSparse([5, 6, 7]);
            var plain = Sparse([5, 6, 7]);
            Assert.Equal(plain, shifted);
            Assert.Equal(plain.GetHashCode(), shifted.GetHashCode());

            sparse.Dispose();
            sparse2.Dispose();
            shifted.Dispose();
            plain.Dispose();
        }

        [Fact]
        public void BoundsFollowEgglog()
        {
            Assert.True(Subset.Dense(4, 4).TryGetBounds(out var empty));
            Assert.Equal(new OffsetRange(4, 4), empty);

            var s = Sparse([3, 3, 8]);
            Assert.True(s.TryGetBounds(out var bounds));
            Assert.Equal(new OffsetRange(3, 9), bounds);
            s.Dispose();

            var e = Sparse([]);
            Assert.False(e.TryGetBounds(out _));
            Assert.True(e.IsEmpty);
            Assert.False(e.IsDense);
            e.Dispose();
        }

        [Fact]
        public void ToStringShowsKindAndRows()
        {
            Assert.Equal("Dense[2..5)", Subset.Dense(2, 5).ToString());
            var s = Sparse([1, 4]);
            Assert.Equal("Sparse[1, 4]", s.ToString());
            s.Dispose();
        }

    }

}

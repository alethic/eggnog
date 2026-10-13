using Xunit;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// Tests for <see cref="ColumnUniqueness"/>: egglog's <c>ColUniqueness</c> ordering and join
    /// (core-relations/src/free_join/mod.rs:930-976).
    /// </summary>
    public class ColumnUniquenessTests
    {

        [Fact]
        public void OrdersByRowsPerValue()
        {
            var unique = new ColumnUniqueness(100, 100);
            var halves = new ColumnUniqueness(100, 50);
            var same = new ColumnUniqueness(10, 5);
            Assert.True(unique.CompareTo(halves) < 0);
            Assert.True(halves.CompareTo(unique) > 0);
            Assert.Equal(0, halves.CompareTo(same));
        }

        [Fact]
        public void JoinMultipliesRowsAndKeepsTheLargerColumn()
        {
            // Equality is by ratio (egglog's PartialEq), so check the fields.
            var a = new ColumnUniqueness(100, 30).Join(new ColumnUniqueness(50, 50));
            Assert.Equal((5000UL, 50UL), (a.TableSize, a.ColumnSize));
            var b = ColumnUniqueness.Identity.Join(new ColumnUniqueness(7, 3));
            Assert.Equal((7UL, 3UL), (b.TableSize, b.ColumnSize));
        }

        [Fact]
        public void EqualityIsTheOrderingsEquality()
        {
            // egglog: PartialEq is cmp(other) == Equal (free_join/mod.rs:957-961).
            var halves = new ColumnUniqueness(100, 50);
            Assert.True(halves == new ColumnUniqueness(10, 5));
            Assert.True(halves != new ColumnUniqueness(100, 100));
            Assert.False(halves.Equals(new ColumnUniqueness(100, 100)));
            Assert.True(halves.Equals((object)new ColumnUniqueness(2, 1)));
            Assert.False(halves.Equals((object)"2/1"));

            // Not transitive at the edges, as in egglog: (0, 0) equals everything, so the hash code is constant.
            var empty = new ColumnUniqueness(0, 0);
            Assert.True(empty == halves);
            Assert.True(empty == new ColumnUniqueness(100, 100));
            Assert.Equal(empty.GetHashCode(), halves.GetHashCode());
            Assert.Equal(halves.GetHashCode(), new ColumnUniqueness(10, 5).GetHashCode());
        }

        [Fact]
        public void ProductsSaturate()
        {
            var huge = new ColumnUniqueness(ulong.MaxValue / 2, 1).Join(new ColumnUniqueness(4, 1));
            Assert.Equal(ulong.MaxValue, huge.TableSize);
            Assert.True(huge.CompareTo(new ColumnUniqueness(10, 5)) > 0);

            // Saturated products compare equal, as in egglog.
            Assert.Equal(0, huge.CompareTo(new ColumnUniqueness(ulong.MaxValue, 3)));
            Assert.Equal(0, new ColumnUniqueness(ulong.MaxValue, 2).CompareTo(new ColumnUniqueness(ulong.MaxValue, 3)));
        }

        [Fact]
        public void EmptyColumnsCompareLikeEgglog()
        {
            // An empty table: 0 rows, 0 values. (0 * b.col) vs (b.table * 0): equal to anything.
            Assert.Equal(0, new ColumnUniqueness(0, 0).CompareTo(new ColumnUniqueness(10, 3)));
        }

    }

}

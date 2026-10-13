using System.Collections.Generic;

using Xunit;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// Tests for <see cref="OffsetRange"/>.
    /// </summary>
    public class OffsetRangeTests
    {

        [Fact]
        public void SizeAndEmptiness()
        {
            Assert.Equal(4, new OffsetRange(3, 7).Size);
            Assert.False(new OffsetRange(3, 7).IsEmpty);
            Assert.True(new OffsetRange(5, 5).IsEmpty);
            Assert.True(default(OffsetRange).IsEmpty);
        }

        [Fact]
        public void EqualityIsByEndpoints()
        {
            Assert.Equal(new OffsetRange(1, 4), new OffsetRange(1, 4));
            Assert.NotEqual(new OffsetRange(2, 2), new OffsetRange(0, 0));
            Assert.Equal(new OffsetRange(1, 4).GetHashCode(), new OffsetRange(1, 4).GetHashCode());
        }

        [Fact]
        public void IteratesAscending()
        {
            var range = new OffsetRange(4, 9);
            var visitor = new CollectingVisitor();
            range.ForEach(ref visitor);
            Assert.Equal([4, 5, 6, 7, 8], visitor.Rows);

            var enumerated = new List<int>();
            foreach (var row in range)
                enumerated.Add(row);

            Assert.Equal(visitor.Rows, enumerated);
        }

        [Fact]
        public void FormatsAsHalfOpenInterval()
        {
            Assert.Equal("[2..5)", new OffsetRange(2, 5).ToString());
        }

    }

}

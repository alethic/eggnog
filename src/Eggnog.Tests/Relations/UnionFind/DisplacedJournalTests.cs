using System;
using System.Linq;

using Xunit;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// Tests for <see cref="DisplacedJournal"/> (colored-contexts spec §5.3): appends in nondecreasing timestamp order,
    /// the id-to-row map, the timestamp bounds shared with <see cref="DisplacedUnionFind"/>, and release.
    /// </summary>
    public class DisplacedJournalTests
    {

        [Fact]
        public void AnEmptyJournalHasNoRows()
        {
            var journal = new DisplacedJournal();
            Assert.Equal(0, journal.Count);
            Assert.Equal(0u, journal.LastTimestamp);
            Assert.True(journal.Ids.IsEmpty);
            Assert.False(journal.Contains(0));
            Assert.False(journal.TryGetRow(5, out var row));
            Assert.Equal(-1, row);
            Assert.Throws<ArgumentOutOfRangeException>(() => journal.GetId(0));
            Assert.Throws<ArgumentOutOfRangeException>(() => journal.GetTimestamp(0));
            Assert.False(journal.TimestampBounds(3, out var start, out var end));
            Assert.Equal((0, 0), (start, end));
        }

        [Fact]
        public void AppendsAreFoundByIdAndRow()
        {
            var journal = new DisplacedJournal();
            journal.Append(9, 1);
            journal.Append(4, 1);
            journal.Append(70, 3);
            Assert.Equal(3, journal.Count);
            Assert.Equal(new uint[] { 9, 4, 70 }, journal.Ids.ToArray());
            Assert.Equal(new uint[] { 1, 1, 3 }, journal.Timestamps.ToArray());
            Assert.Equal(3u, journal.LastTimestamp);
            Assert.True(journal.TryGetRow(4, out var row));
            Assert.Equal(1, row);
            Assert.Equal(70u, journal.GetId(2));
            Assert.Equal(3u, journal.GetTimestamp(2));
            Assert.True(journal.Contains(9));
            Assert.False(journal.Contains(5));
        }

        [Fact]
        public void ADecreasingTimestampIsRefusedWithTheUnionFindsMessage()
        {
            var journal = new DisplacedJournal();
            journal.Append(9, 5);
            var error = Assert.Throws<InvalidOperationException>(() => journal.Append(4, 4));
            var uf = new DisplacedUnionFind();
            uf.Union(1, 2, 5, out _, out _);
            var expected = Assert.Throws<InvalidOperationException>(() => uf.Union(3, 4, 4, out _, out _));
            Assert.Equal(expected.Message, error.Message);
            Assert.Equal(1, journal.Count);
            Assert.False(journal.Contains(4));

            // Equal timestamps are fine.
            journal.Append(4, 5);
            Assert.Equal(2, journal.Count);
        }

        [Fact]
        public void TimestampBoundsAgreeWithTheUnionFinds()
        {
            var random = new Random(3);
            var journal = new DisplacedJournal();
            var uf = new DisplacedUnionFind();
            uint ts = 1;
            uint next = 1000;
            for (var i = 0; i < 300; i++)
            {
                ts += (uint)random.Next(3);
                journal.Append(next, ts);
                Assert.True(uf.Union(0, next, ts, out _, out _));
                next++;
            }

            for (uint t = 0; t <= ts + 1; t++)
            {
                var found = journal.TimestampBounds(t, out var start, out var end);
                Assert.Equal(uf.TimestampBounds(t, out var ufStart, out var ufEnd), found);
                Assert.Equal((ufStart, ufEnd), (start, end));
            }
        }

        [Fact]
        public void ManyAppendsGrowTheStorage()
        {
            var journal = new DisplacedJournal();
            for (uint i = 0; i < 10_000; i++)
                journal.Append(i * 7 + 3, i / 10);

            Assert.Equal(10_000, journal.Count);
            for (uint i = 0; i < 10_000; i += 97)
            {
                Assert.True(journal.TryGetRow(i * 7 + 3, out var row));
                Assert.Equal((int)i, row);
            }
        }

        [Fact]
        public void ReleaseDropsEveryRow()
        {
            var journal = new DisplacedJournal();
            journal.Append(9, 1);
            journal.Release();
            Assert.Equal(0, journal.Count);
            Assert.False(journal.Contains(9));
        }

    }

}

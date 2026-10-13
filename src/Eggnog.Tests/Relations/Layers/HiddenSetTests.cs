using System;
using System.Collections.Generic;
using System.Linq;

using Xunit;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// Tests for <see cref="HiddenSet"/>: random add and seal sequences against a <see cref="SortedSet{T}"/>, the
    /// switch to a bitset, unions, and a sealed set reopening.
    /// </summary>
    public class HiddenSetTests
    {

        public static TheoryData<int> Seeds()
        {
            var data = new TheoryData<int>();
            for (var seed = 0; seed < 1000; seed++)
                data.Add(seed);

            return data;
        }

        /// <summary>
        /// A random sequence of adds and seals over a layer that grows: <see cref="HiddenSet.Contains"/> always agrees
        /// with the reference, <see cref="HiddenSet.ContainsSealed"/> agrees whenever the set is sealed, and the set only
        /// grows: nothing hidden is ever un-hidden.
        /// </summary>
        [Theory]
        [MemberData(nameof(Seeds))]
        public void AddAndSealAgreeWithASortedSet(int seed)
        {
            var random = new Random(seed);
            var rows = random.Next(1, 3000);
            var set = new HiddenSet(3);
            var reference = new SortedSet<int>();
            var steps = random.Next(1, 400);
            for (var step = 0; step < steps; step++)
            {
                if (random.Next(10) == 0)
                    rows += random.Next(0, 200);

                if (random.Next(6) == 0)
                {
                    set.Seal(rows);
                    Assert.True(set.IsSealed);
                    for (var probe = 0; probe < 40; probe++)
                    {
                        var row = random.Next(-2, rows + 70);
                        Assert.Equal(reference.Contains(row), set.ContainsSealed(row));
                    }

                    continue;
                }

                // Dense runs push some sets over the bitset threshold.
                var added = random.Next(3) == 0 ? random.Next(Math.Max(1, rows / 4)) : random.Next(rows);
                var count = random.Next(1, random.Next(2) == 0 ? 2 : 50);
                for (var i = 0; i < count && added + i < rows; i++)
                {
                    var version = set.Version;
                    var expected = reference.Add(added + i);
                    Assert.Equal(expected, set.Add(added + i, rows));
                    Assert.Equal(expected ? version + 1 : version, set.Version);
                }

                Assert.Equal(reference.Count, set.Count);
                for (var probe = 0; probe < 20; probe++)
                {
                    var row = random.Next(-2, rows + 70);
                    Assert.Equal(reference.Contains(row), set.Contains(row));
                }
            }

            set.Seal(rows);
            var copied = new List<int>();
            set.CopyTo(copied);
            Assert.Equal(reference.ToArray(), copied.ToArray());
            Assert.Equal(3, set.Generation);
            if (set.IsBitset == false)
                Assert.Equal(reference.ToArray(), set.SortedRows!.Take(set.SortedCount).ToArray());
        }

        [Fact]
        public void ASetBecomesABitsetOnceItIsDense()
        {
            var set = new HiddenSet(0);
            for (var row = 0; row < HiddenSet.MinBitsetCount - 1; row++)
                set.Add(row, 1000);

            set.Seal(1000);
            Assert.False(set.IsBitset);

            // 64 rows of a 1000-row layer: more than 1000 / 32, so the bitset is smaller.
            set.Add(500, 1000);
            set.Seal(1000);
            Assert.True(set.IsBitset);
            Assert.Null(set.SortedRows);
            Assert.Equal(0, set.SortedCount);
            Assert.NotNull(set.Bits);
            Assert.True(set.ContainsSealed(500));
            Assert.False(set.ContainsSealed(501));
            Assert.False(set.ContainsSealed(1 << 20));

            // A sparse set over a large layer stays a sorted array.
            var sparse = new HiddenSet(0);
            for (var row = 0; row < 100; row++)
                sparse.Add(row * 1000, 1_000_000);

            sparse.Seal(1_000_000);
            Assert.False(sparse.IsBitset);
            Assert.Equal(100, sparse.SortedCount);
        }

        [Fact]
        public void ASealedSetReopensAndPublishesANewSnapshot()
        {
            var set = new HiddenSet(0);
            set.Add(5, 10);
            set.Seal(10);
            var snapshot = set.SortedRows;
            Assert.True(set.IsSealed);

            // Adding reopens the tail: the any-state probe sees it, the sealed part does not change until the seal.
            Assert.True(set.Add(7, 10));
            Assert.False(set.IsSealed);
            Assert.True(set.Contains(7));
            Assert.Same(snapshot, set.SortedRows);
            Assert.False(set.Add(5, 10));
            Assert.False(set.Add(7, 10));

            set.Seal(10);
            Assert.True(set.IsSealed);
            Assert.NotSame(snapshot, set.SortedRows);
            Assert.Equal(new[] { 5 }, snapshot);
            Assert.Equal(new[] { 5, 7 }, set.SortedRows);
            Assert.True(set.ContainsSealed(7));

            // The same for a bitset: the sealed bits are replaced, not changed in place.
            var dense = new HiddenSet(0);
            for (var row = 0; row < 64; row++)
                dense.Add(row, 64);

            dense.Seal(64);
            var bits = dense.Bits;
            dense.Add(64, 65);
            dense.Seal(65);
            Assert.NotSame(bits, dense.Bits);
            Assert.True(dense.ContainsSealed(64));
            Assert.Equal(65, dense.Count);
        }

        [Fact]
        public void UnionsMatchTheReference()
        {
            var random = new Random(7);
            for (var trial = 0; trial < 300; trial++)
            {
                var rows = random.Next(1, 2000);
                var sets = new HiddenSet[random.Next(1, 4)];
                var reference = new SortedSet<int>();
                for (var i = 0; i < sets.Length; i++)
                {
                    sets[i] = new HiddenSet(2);
                    var n = random.Next(0, random.Next(2) == 0 ? 10 : rows);
                    for (var k = 0; k < n; k++)
                    {
                        var row = random.Next(rows);
                        sets[i].Add(row, rows);
                        reference.Add(row);
                    }

                    sets[i].Seal(rows);
                }

                var union = HiddenSet.Union(sets, rows);
                Assert.True(union.IsSealed);
                Assert.Equal(2, union.Generation);
                Assert.Equal(reference.Count, union.Count);
                var copied = new List<int>();
                union.CopyTo(copied);
                Assert.Equal(reference.ToArray(), copied.ToArray());
                for (var row = 0; row < rows + 5; row += 1 + random.Next(5))
                    Assert.Equal(reference.Contains(row), union.ContainsSealed(row));
            }

            Assert.Throws<ArgumentException>(() => HiddenSet.Union(ReadOnlySpan<HiddenSet>.Empty, 1));
        }

        [Fact]
        public void LiveRowsAreCountedOnce()
        {
            // Rows 0..3 of a two-column layer; row 2 is a tombstone.
            var data = new uint[] { 1, 0, 2, 0, Value.StaleRep, 0, 4, 0 };
            var frozen = new HiddenSet(0);
            frozen.Add(0, 4);
            frozen.Add(2, 4);
            frozen.Seal(4);
            var live = new HiddenSet(0);
            live.Add(0, 4);
            live.Add(3, 4);
            live.Seal(4);
            Assert.Equal(1, frozen.CountLive(data, 2, 4, null));
            Assert.Equal(2, new HiddenRows(frozen, live).CountLive(data, 2, 4));
            Assert.Equal(0, HiddenRows.None.CountLive(data, 2, 4));
        }

    }

}

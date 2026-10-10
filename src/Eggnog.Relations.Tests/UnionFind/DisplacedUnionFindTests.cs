using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

using Xunit;

namespace Eggnog.Relations.Tests
{

    public class DisplacedUnionFindTests
    {

        static (uint Child, uint Canonical, uint Timestamp) Row(DisplacedUnionFind d, int row)
        {
            d.Expand(row, out var child, out var canonical, out var timestamp);
            return (child, canonical, timestamp);
        }

        /// <summary>
        /// Port of <c>core-relations/src/uf/tests.rs</c> <c>displaced</c>, at the storage level: the scans become row
        /// expansions and the column-0 equality refinement becomes the child-to-row lookup.
        /// </summary>
        [Fact]
        public void Displaced()
        {
            var d = new DisplacedUnionFind();
            Assert.True(d.Union(0, 1, 0, out _, out _));
            Assert.True(d.Union(2, 3, 0, out _, out _));

            var updates = new List<(uint, uint)>();
            for (var row = 0; row < d.Count; row++)
            {
                var (child, canonical, timestamp) = Row(d, row);
                Assert.Equal(0u, timestamp);
                updates.Add((child, canonical));
            }

            Assert.Equal(2, updates.Count);
            Assert.NotEqual(updates[0], updates[1]);

            Assert.True(d.TryGetRow(updates[0].Item1, out var found));
            Assert.Equal((updates[0].Item1, updates[0].Item2, 0u), Row(d, found));

            Assert.True(d.Union(1, 3, 1, out _, out _));

            var canonicals = Enumerable.Range(0, d.Count).Select(r => Row(d, r).Canonical).ToArray();
            Assert.Equal(3, canonicals.Length);
            Assert.All(canonicals, c => Assert.Equal(canonicals[0], c));
        }

        [Fact]
        public void UnionDisplacesTheLargerRootWhateverTheArgumentOrder()
        {
            var d = new DisplacedUnionFind();
            Assert.True(d.Union(1, 0, 5, out var parent, out var child));
            Assert.Equal((0u, 1u), (parent, child));
            Assert.True(d.Union(2, 7, 5, out parent, out child));
            Assert.Equal((2u, 7u), (parent, child));

            // Neither argument is a root: the roots 0 and 2 are what get linked.
            Assert.True(d.Union(7, 1, 6, out parent, out child));
            Assert.Equal((0u, 2u), (parent, child));

            Assert.Equal(new uint[] { 1, 7, 2 }, d.Children.ToArray());
            Assert.Equal(new uint[] { 5, 5, 6 }, d.Timestamps.ToArray());
            Assert.Equal((7u, 0u, 5u), Row(d, 1));
            Assert.Equal(0u, d.GetCanonical(2));
            Assert.Equal(2u, d.GetChild(2));
            Assert.Equal(6u, d.GetTimestamp(2));
        }

        [Fact]
        public void RedundantUnionAppendsNothingAndIgnoresTheTimestamp()
        {
            var d = new DisplacedUnionFind();
            d.Union(3, 4, 10, out _, out _);
            Assert.False(d.Union(4, 3, 2, out var parent, out var child));
            Assert.Equal((3u, 3u), (parent, child));
            Assert.Equal(1, d.Count);
        }

        [Fact]
        public void DecreasingTimestampIsRejectedWithoutChangingAnything()
        {
            var d = new DisplacedUnionFind();
            d.Union(0, 1, 10, out _, out _);
            var ex = Assert.Throws<InvalidOperationException>(() => d.Union(2, 3, 9, out _, out _));
            Assert.Contains("timestamp", ex.Message);
            Assert.Equal(1, d.Count);
            Assert.Equal(3u, d.FindNaive(3));
            Assert.False(d.TryGetRow(3, out _));

            // An equal timestamp is fine.
            Assert.True(d.Union(2, 3, 10, out _, out _));
        }

        [Fact]
        public void LookupFindsExactlyTheDisplacedIds()
        {
            var d = new DisplacedUnionFind();
            d.Union(10, 20, 1, out _, out _);
            d.Union(20, 30, 1, out _, out _);

            Assert.True(d.TryGetRow(20, out var row));
            Assert.Equal(0, row);
            Assert.True(d.TryGetRow(30, out row));
            Assert.Equal(1, row);

            // The root, an id inside the union-find that was never displaced, and an id never seen.
            Assert.False(d.TryGetRow(10, out row));
            Assert.Equal(-1, row);
            Assert.False(d.TryGetRow(15, out _));
            Assert.False(d.TryGetRow(1_000_000, out _));
        }

        [Fact]
        public void FindNaiveOfAnUnseenIdIsTheId()
        {
            var d = new DisplacedUnionFind();
            Assert.Equal(12345u, d.FindNaive(12345));
            d.Union(1, 2, 0, out _, out _);
            Assert.Equal(12345u, d.FindNaive(12345));
            Assert.Equal(1u, d.FindNaive(2));
            Assert.Equal(1u, d.Find(2));
        }

        [Fact]
        public void RowAccessorsRejectRowsOutOfRange()
        {
            var d = new DisplacedUnionFind();
            d.Union(0, 1, 0, out _, out _);
            Assert.Throws<ArgumentOutOfRangeException>(() => d.GetChild(1));
            Assert.Throws<ArgumentOutOfRangeException>(() => d.GetTimestamp(-1));
            Assert.Throws<ArgumentOutOfRangeException>(() => d.GetCanonical(5));
            Assert.Throws<ArgumentOutOfRangeException>(() => d.Expand(1, out _, out _, out _));
        }

        [Fact]
        public void TimestampBoundsFindRunsAndInsertionPoints()
        {
            var d = new DisplacedUnionFind();
            uint[] ts = [1, 1, 3, 3, 3, 7];
            for (var i = 0; i < ts.Length; i++)
                Assert.True(d.Union(0, (uint)i + 1, ts[i], out _, out _));

            static (bool, int, int) Bounds(DisplacedUnionFind d, uint t) => (d.TimestampBounds(t, out var s, out var e), s, e);

            Assert.Equal((false, 0, 0), Bounds(d, 0));
            Assert.Equal((true, 0, 2), Bounds(d, 1));
            Assert.Equal((false, 2, 2), Bounds(d, 2));
            Assert.Equal((true, 2, 5), Bounds(d, 3));
            Assert.Equal((false, 5, 5), Bounds(d, 4));
            Assert.Equal((true, 5, 6), Bounds(d, 7));
            Assert.Equal((false, 6, 6), Bounds(d, 8));
            Assert.Equal((false, 6, 6), Bounds(d, uint.MaxValue));

            Assert.Equal((false, 0, 0), Bounds(new DisplacedUnionFind(), 3));
        }

        /// <summary>
        /// egglog's clear leaves its child-to-row map behind (<c>uf/mod.rs:286-289</c>); here a cleared instance
        /// answers as if new, and rows appended after the clear are found by the lookup.
        /// </summary>
        [Fact]
        public void ClearForgetsRowsLookupsAndUnions()
        {
            var d = new DisplacedUnionFind();
            d.Union(0, 1, 4, out _, out _);
            d.Union(2, 3, 5, out _, out _);
            d.Clear();

            Assert.Equal(0, d.Count);
            Assert.False(d.TryGetRow(1, out _));
            Assert.False(d.TryGetRow(3, out _));
            Assert.Equal(1u, d.FindNaive(1));
            Assert.Equal(3u, d.FindNaive(3));

            // Timestamps restart too.
            Assert.True(d.Union(3, 2, 0, out _, out var child));
            Assert.Equal(3u, child);
            Assert.True(d.TryGetRow(3, out var row));
            Assert.Equal(0, row);
            Assert.False(d.TryGetRow(1, out _));
        }

        [Fact]
        public void CloneIsIndependent()
        {
            var d = new DisplacedUnionFind();
            d.Union(0, 1, 1, out _, out _);
            var copy = d.Clone();
            copy.Union(1, 2, 2, out _, out _);
            d.Union(5, 6, 3, out _, out _);

            Assert.Equal(2, copy.Count);
            Assert.Equal(2, d.Count);
            Assert.True(copy.TryGetRow(2, out _));
            Assert.False(d.TryGetRow(2, out _));
            Assert.False(copy.TryGetRow(6, out _));
            Assert.Equal(0u, copy.FindNaive(2));
            Assert.Equal(2u, d.FindNaive(2));
            Assert.Equal(new uint[] { 1, 2 }, copy.Timestamps.ToArray());
        }

        /// <summary>
        /// Random unions at non-decreasing timestamps, checked against a naive partition: each union displaces the
        /// larger of the two class minimums, every non-minimum id has exactly one row, every row's canonical column is
        /// its class minimum, and timestamp bounds match a linear scan. The parent array is also checked, after every
        /// union, against a transliteration of egglog's <c>insert_impl</c> including the two finds the port omits.
        /// </summary>
        [Theory]
        [InlineData(1, 50)]
        [InlineData(2, 400)]
        [InlineData(3, 3000)]
        public void RandomUnionsMatchThePartition(int seed, int size)
        {
            var random = new Random(seed);
            var d = new DisplacedUnionFind();
            var partition = new PartitionReference(size);
            var rust = new RustUnionFindReference();
            var expectedChildren = new List<uint>();
            var expectedTimestamps = new List<uint>();
            uint now = 0;

            for (var step = 0; step < 4 * size; step++)
            {
                if (random.Next(10) == 0)
                    now += (uint)random.Next(1, 4);

                var a = (uint)random.Next(size);
                var b = (uint)random.Next(size);
                var distinct = partition.Union(a, b, out var p, out var c);
                Assert.Equal(distinct, d.Union(a, b, now, out var parent, out var child));
                Assert.Equal((p, c), (parent, child));
                if (distinct)
                {
                    expectedChildren.Add(c);
                    expectedTimestamps.Add(now);
                }

                // egglog's insert_impl (uf/mod.rs:491-498) on the reference.
                if (rust.Find(a) != rust.Find(b))
                {
                    var (rp, rc) = rust.Union(a, b);
                    rust.Find(rp);
                    rust.Find(rc);
                }

                Assert.True(rust.Parents.SequenceEqual(d.UnionFind.Parents.ToArray()));
            }

            Assert.Equal(expectedChildren, d.Children.ToArray());
            Assert.Equal(expectedTimestamps, d.Timestamps.ToArray());

            for (uint id = 0; id < size; id++)
            {
                var canonical = partition.Canonical(id);
                Assert.Equal(canonical, d.FindNaive(id));
                Assert.Equal(canonical != id, d.TryGetRow(id, out var row));
                if (canonical != id)
                    Assert.Equal((id, canonical, expectedTimestamps[row]), Row(d, row));
            }

            for (uint t = 0; t <= now + 1; t++)
            {
                var first = expectedTimestamps.FindIndex(x => x >= t);
                if (first < 0)
                    first = expectedTimestamps.Count;
                var last = expectedTimestamps.FindIndex(x => x > t);
                if (last < 0)
                    last = expectedTimestamps.Count;

                Assert.Equal(first != last, d.TimestampBounds(t, out var start, out var end));
                Assert.Equal((first, last), (start, end));
            }

            // Many readers at once, no writer.
            Parallel.For(0, size, i => Assert.Equal(partition.Canonical((uint)i), d.FindNaive((uint)i)));
        }

        /// <summary>
        /// Random unions whose timestamps wander backwards, over ids that keep reaching past the reserved range. A
        /// union that goes backwards between distinct classes must throw and leave the parent array, the reserved
        /// count and the rows exactly as they were; every other union, a backwards redundant one included, must leave
        /// the parent array exactly where egglog's <c>insert_impl</c> (<c>uf/mod.rs:489-510</c>) leaves it.
        /// </summary>
        [Theory]
        [InlineData(4, 40)]
        [InlineData(5, 300)]
        [InlineData(6, 2000)]
        public void BackwardTimestampsChangeNothingUnlessTheUnionIsRedundant(int seed, int size)
        {
            var random = new Random(seed);
            var d = new DisplacedUnionFind();
            var partition = new PartitionReference(size);
            var rust = new RustUnionFindReference();
            var rejected = 0;
            var redundantBackwards = 0;
            uint now = 10;

            for (var step = 0; step < 4 * size; step++)
            {
                now = (uint)Math.Max(0, (int)now + random.Next(-2, 4));
                var bound = Math.Max(2, (int)((long)size * (step + 1) / (4 * size)) + 2);
                var a = (uint)random.Next(Math.Min(size, bound));
                var b = (uint)random.Next(Math.Min(size, bound));
                var backwards = d.Count > 0 && d.GetTimestamp(d.Count - 1) > now;
                var distinct = partition.Canonical(a) != partition.Canonical(b);

                if (backwards && distinct)
                {
                    var idCount = d.UnionFind.IdCount;
                    var parents = d.UnionFind.Parents.ToArray();
                    var children = d.Children.ToArray();
                    Assert.Throws<InvalidOperationException>(() => d.Union(a, b, now, out _, out _));
                    Assert.Equal(idCount, d.UnionFind.IdCount);
                    Assert.Equal(parents, d.UnionFind.Parents.ToArray());
                    Assert.Equal(children, d.Children.ToArray());
                    Assert.Equal(partition.Canonical(a), d.FindNaive(a));
                    Assert.Equal(partition.Canonical(b), d.FindNaive(b));
                    rejected++;
                    continue;
                }

                if (backwards)
                    redundantBackwards++;

                partition.Union(a, b, out var p, out var c);
                Assert.Equal(distinct, d.Union(a, b, now, out var parent, out var child));
                Assert.Equal((p, c), (parent, child));

                // egglog's insert_impl, on a call that gets past its assertion.
                if (rust.Find(a) != rust.Find(b))
                {
                    var (rp, rc) = rust.Union(a, b);
                    rust.Find(rp);
                    rust.Find(rc);
                }

                Assert.Equal(rust.Parents.Count, d.UnionFind.IdCount);
                Assert.True(rust.Parents.SequenceEqual(d.UnionFind.Parents.ToArray()));
            }

            Assert.True(rejected > 0);
            Assert.True(redundantBackwards > 0);

            // The log still holds one row per displaced id, in timestamp order.
            var timestamps = d.Timestamps.ToArray();
            for (var i = 1; i < timestamps.Length; i++)
                Assert.True(timestamps[i - 1] <= timestamps[i]);

            for (uint id = 0; id < size; id++)
                Assert.Equal(partition.Canonical(id) != id, d.TryGetRow(id, out _));
        }

    }

}

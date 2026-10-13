using System;
using System.Collections.Generic;
using System.Linq;

using Xunit;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// Tests for <see cref="FlattenedUnionFind"/>, a frozen chain of overlay levels collapsed into one map: <c>null</c>
    /// when nothing moves, the dense representation up to exactly <see cref="FlattenedUnionFind.DenseFactor"/> times
    /// the entries and the hash table beyond, identity outside the domain, the collected keys, and flattening a chain.
    /// </summary>
    public class FlattenedUnionFindTests
    {

        [Fact]
        public void NothingStoredIsNull()
        {
            Assert.Null(FlattenedUnionFind.FromPairs([], []));
            var root = new DisplacedUnionFind();
            Assert.Null(FlattenedUnionFind.Build(new LayeredUnionFind(root)));
            Assert.Null(FlattenedUnionFind.Build(new LayeredUnionFind(root).Extend(new UnionFindOverlay())));
            Assert.Throws<ArgumentException>(() => FlattenedUnionFind.FromPairs([1], []));
        }

        /// <summary>
        /// Two entries whose keys span exactly <c>range</c> ids.
        /// </summary>
        static FlattenedUnionFind Spanning(uint first, int range) => FlattenedUnionFind.FromPairs([first, first + (uint)range - 1], [1, 2])!;

        [Fact]
        public void TheDenseBoundaryIsEightTimesTheEntries()
        {
            // Two entries: a range of 16 ids is dense, 17 is hashed.
            var dense = Spanning(100, 2 * FlattenedUnionFind.DenseFactor);
            Assert.True(dense.IsDense);
            var hashed = Spanning(100, 2 * FlattenedUnionFind.DenseFactor + 1);
            Assert.False(hashed.IsDense);

            foreach (var map in new[] { dense, hashed })
            {
                Assert.Equal(2, map.Count);
                Assert.Equal(1u, map.Map(100));
                Assert.Equal(2u, map.Map(map.IsDense ? 115u : 116u));

                // Identity outside the domain, inside and outside the range.
                Assert.Equal(101u, map.Map(101));
                Assert.Equal(99u, map.Map(99));
                Assert.Equal(5000u, map.Map(5000));
                Assert.Equal(0u, map.Map(0));
                Assert.Equal(uint.MaxValue, map.Map(uint.MaxValue));
            }

            // A single entry is dense.
            Assert.True(FlattenedUnionFind.FromPairs([7], [3])!.IsDense);
        }

        [Fact]
        public void CollectKeysListsWhatMoves()
        {
            foreach (var range in new[] { 16, 17 })
            {
                var map = Spanning(100, range);
                var keys = new List<uint>();
                map.CollectKeys(keys);
                Assert.Equal(new uint[] { 100, 100 + (uint)range - 1 }, keys.OrderBy(k => k).ToArray());
            }
        }

        [Theory]
        [InlineData(1)]
        [InlineData(2)]
        [InlineData(3)]
        public void BuildTabulatesTheChainAboveTheRoot(int seed)
        {
            const int Size = 500;
            var random = new Random(seed);
            var root = new DisplacedUnionFind();
            uint ts = 1;
            for (var i = 0; i < 100; i++)
                root.Union((uint)random.Next(Size), (uint)random.Next(Size), ts++, out _, out _);

            var o1 = new UnionFindOverlay();
            var c1 = new LayeredUnionFind(root).Extend(o1);
            for (var i = 0; i < 50; i++)
                o1.Union(root.FindNaive((uint)random.Next(Size)), root.FindNaive((uint)random.Next(Size)), out _, out _);

            var o2 = new UnionFindOverlay();
            var c2 = c1.Extend(o2);
            for (var i = 0; i < 50; i++)
                o2.Union(c1.Find((uint)random.Next(Size)), c1.Find((uint)random.Next(Size)), out _, out _);

            var map = FlattenedUnionFind.Build(c2)!;
            Assert.NotNull(map);
            for (uint x = 0; x < Size; x++)
            {
                var rootCanonical = root.FindNaive(x);
                Assert.Equal(c2.MapAboveRoot(rootCanonical), map.Map(rootCanonical));
            }

            // Every id the map moves is a member of a level, and moves.
            var keys = new List<uint>();
            map.CollectKeys(keys);
            Assert.Equal(map.Count, keys.Count);
            Assert.All(keys, k => Assert.NotEqual(k, map.Map(k)));
        }

    }

}

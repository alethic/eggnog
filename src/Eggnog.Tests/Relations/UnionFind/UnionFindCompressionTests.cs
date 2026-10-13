using System;
using System.Linq;

using Xunit;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// Tests for the root's path compression: <see cref="UnionFind.CompressPath"/> and
    /// <see cref="UnionFind.CompressAll"/> keep every root and <c>parents[i] ≤ i</c>;
    /// <see cref="DisplacedUnionFind"/>'s <c>CompressRows</c> and <c>CompressAll</c>; and
    /// <see cref="DisplacedTable"/>'s merge compressing the ids it displaced, invisibly to every result.
    /// </summary>
    public class UnionFindCompressionTests
    {

        /// <summary>
        /// Builds the chain 9 → 8 → ... → 0 (each id's parent is the next smaller one) by union-by-min.
        /// </summary>
        static UnionFind Chain(int length)
        {
            var uf = new UnionFind();
            for (var i = length - 1; i > 0; i--)
                uf.Union((uint)i, (uint)(i - 1));

            return uf;
        }

        static int Depth(UnionFind uf, uint id)
        {
            var depth = 0;
            var p = uf.Parents;
            while (p[(int)id] != id)
            {
                id = p[(int)id];
                depth++;
            }

            return depth;
        }

        [Fact]
        public void CompressPathPointsThePathAtTheRoot()
        {
            var uf = Chain(10);
            Assert.Equal(9, Depth(uf, 9));
            uf.CompressPath(9);
            for (uint i = 1; i < 10; i++)
                Assert.Equal(1, Depth(uf, i));

            Assert.All(Enumerable.Range(0, 10), i => Assert.Equal(0u, uf.FindNaive((uint)i)));

            // Unreserved ids and roots are left alone.
            uf.CompressPath(500);
            uf.CompressPath(0);
            Assert.Equal(10, uf.IdCount);
        }

        [Fact]
        public void CompressPathLeavesOtherBranchesAlone()
        {
            var uf = new UnionFind();
            uf.Union(5, 4);
            uf.Union(4, 3);
            uf.Union(7, 6);
            uf.Union(6, 3);
            var before = uf.Parents.ToArray();
            uf.CompressPath(5);
            var after = uf.Parents.ToArray();
            Assert.Equal(before[7], after[7]);
            Assert.Equal(3u, after[5]);
            Assert.Equal(3u, uf.FindNaive(7));
        }

        [Theory]
        [InlineData(1)]
        [InlineData(2)]
        [InlineData(3)]
        public void CompressionKeepsEveryRootAndTheInvariant(int seed)
        {
            const int Size = 2000;
            var random = new Random(seed);
            var uf = new UnionFind();
            var reference = new PartitionReference(Size);
            for (var i = 0; i < 1500; i++)
            {
                var a = (uint)random.Next(Size);
                var b = (uint)random.Next(Size);
                uf.Union(a, b);
                reference.Union(a, b, out _, out _);
                if (i % 7 == 0)
                    uf.CompressPath((uint)random.Next(Size));
            }

            void Check()
            {
                var parents = uf.Parents;
                for (var i = 0; i < parents.Length; i++)
                {
                    Assert.True(parents[i] <= i);
                    Assert.Equal(reference.Canonical((uint)i), uf.FindNaive((uint)i));
                }
            }

            Check();
            uf.CompressAll();
            Check();

            // After a full compression every id points straight at its root.
            var compressed = uf.Parents;
            for (var i = 0; i < compressed.Length; i++)
                Assert.Equal(compressed[(int)compressed[i]], compressed[i]);
        }

        [Fact]
        public void CompressAllOfAnEmptyUnionFindDoesNothing()
        {
            var uf = new UnionFind();
            uf.CompressAll();
            Assert.Equal(0, uf.IdCount);
        }

        [Fact]
        public void DisplacedUnionFindCompressesRowsAndAll()
        {
            var uf = new DisplacedUnionFind();
            for (var i = 9; i > 0; i--)
                uf.Union((uint)i, (uint)(i - 1), 1, out _, out _);

            var rows = uf.Count;
            Assert.Equal(1u, uf.LastTimestamp);
            Assert.Throws<ArgumentOutOfRangeException>(() => uf.CompressRows(rows + 1));
            uf.CompressRows(rows);
            uf.CompressRows(rows - 1);

            // The last row displaced 1; its path was one hop already. The first row displaced 9, deepest of all.
            uf.CompressRows(0);
            Assert.All(uf.UnionFind.Parents.ToArray(), p => Assert.Equal(0u, p));
            Assert.Equal(0u, new DisplacedUnionFind().LastTimestamp);

            var fresh = new DisplacedUnionFind();
            for (var i = 9; i > 0; i--)
                fresh.Union((uint)i, (uint)(i - 1), 1, out _, out _);

            fresh.CompressAll();
            Assert.All(fresh.UnionFind.Parents.ToArray(), p => Assert.Equal(0u, p));
        }

        [Fact]
        public void TheTablesMergeCompressesTheIdsItDisplaced()
        {
            // One merge builds the chain 9 → 8 → ... → 0; afterwards every id it displaced points at the root, and the
            // rows and finds are exactly what they would be without compression.
            var table = new DisplacedTable();
            var buffer = table.NewBuffer();
            for (var i = 9; i > 0; i--)
                buffer.StageInsert([(uint)i, (uint)(i - 1), 1]);

            buffer.Flush();
            var reference = new DisplacedUnionFind();
            for (var i = 9; i > 0; i--)
                reference.Union((uint)i, (uint)(i - 1), 1, out _, out _);

            var change = table.Merge(TableTestUtil.EmptyState());
            Assert.True(change.Added);
            Assert.All(table.UnionFind.UnionFind.Parents.ToArray(), p => Assert.Equal(0u, p));
            Assert.Equal(reference.Children.ToArray(), table.UnionFind.Children.ToArray());
            Assert.Equal(reference.Timestamps.ToArray(), table.UnionFind.Timestamps.ToArray());
            for (uint i = 0; i < 12; i++)
                Assert.Equal(reference.FindNaive(i), table.FindNaive(i));

            // A merge that changes nothing compresses nothing.
            var unchanged = table.Merge(TableTestUtil.EmptyState());
            Assert.False(unchanged.Added);
        }

    }

}

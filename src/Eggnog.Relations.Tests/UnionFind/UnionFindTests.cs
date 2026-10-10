using System;
using System.Linq;
using System.Threading.Tasks;

using Xunit;

namespace Eggnog.Relations.Tests
{

    public class UnionFindTests
    {

        static uint[] Range(int start, int count) => Enumerable.Range(start, count).Select(i => (uint)i).ToArray();

        /// <summary>
        /// Port of <c>union-find/src/tests.rs</c> <c>basic_uf</c>.
        /// </summary>
        [Fact]
        public void BasicUnionFind()
        {
            var uf = new UnionFind();
            var ids1 = Range(0, 100);
            var ids2 = Range(100, 100);

            foreach (var ids in new[] { ids1, ids2 })
            {
                for (var i = 0; i + 1 < ids.Length; i++)
                {
                    var (parent, child) = uf.Union(ids[i], ids[i + 1]);
                    Assert.Equal(parent, uf.FindNaive(ids[i]));
                    Assert.Equal(parent, uf.FindNaive(ids[i + 1]));
                    Assert.Equal(parent, uf.Find(ids[i]));
                    Assert.Equal(parent, uf.Find(ids[i + 1]));
                    Assert.True(child == ids[i] || child == ids[i + 1]);
                }
            }

            for (var i = 0; i + 1 < ids1.Length; i++)
                Assert.Equal(uf.Find(ids1[i]), uf.Find(ids1[i + 1]));
            for (var i = 0; i + 1 < ids2.Length; i++)
                Assert.Equal(uf.Find(ids2[i]), uf.Find(ids2[i + 1]));
            Assert.NotEqual(uf.Find(ids1[0]), uf.Find(ids2[0]));

            uf.Union(ids1[5], ids2[20]);

            var target = uf.Find(ids1[0]);
            Assert.All(ids1.Concat(ids2), id => Assert.Equal(target, uf.Find(id)));
        }

        [Fact]
        public void UnionLinksTheLargerRootUnderTheSmaller()
        {
            var uf = new UnionFind();
            Assert.Equal((3u, 7u), uf.Union(7, 3));
            Assert.Equal((1u, 9u), uf.Union(9, 1));
            Assert.Equal((1u, 3u), uf.Union(7, 9));
            Assert.Equal((1u, 1u), uf.Union(3, 9));
            Assert.Equal(1u, uf.Find(7));
            Assert.Equal(1u, uf.FindNaive(3));
        }

        [Fact]
        public void UnionOfOneClassReturnsItsRootTwice()
        {
            var uf = new UnionFind();
            Assert.Equal((5u, 5u), uf.Union(5, 5));
            uf.Union(2, 5);
            Assert.Equal((2u, 2u), uf.Union(5, 2));
        }

        [Fact]
        public void FindNaiveTreatsUnreservedIdsAsRootsAndWritesNothing()
        {
            var uf = new UnionFind();
            Assert.Equal(0, uf.IdCount);
            Assert.Equal(1000u, uf.FindNaive(1000));
            Assert.Equal(0, uf.IdCount);

            uf.Union(10, 20);
            Assert.Equal(21, uf.IdCount);
            Assert.Equal(10u, uf.FindNaive(20));
            Assert.Equal(1000u, uf.FindNaive(1000));
            Assert.Equal(uint.MaxValue, uf.FindNaive(uint.MaxValue));
            Assert.Equal(21, uf.IdCount);
        }

        [Fact]
        public void FindReservesTheId()
        {
            var uf = new UnionFind();
            Assert.Equal(40u, uf.Find(40));
            Assert.Equal(41, uf.IdCount);
            Assert.Equal(3u, uf.Find(3));
            Assert.Equal(41, uf.IdCount);
        }

        [Fact]
        public void ReserveAddsIdentitySlots()
        {
            var uf = new UnionFind();
            uf.Reserve(4);
            Assert.Equal(new uint[] { 0, 1, 2, 3, 4 }, uf.Parents.ToArray());
            uf.Reserve(2);
            Assert.Equal(5, uf.IdCount);
            uf.Reserve(1000);
            Assert.Equal(1001, uf.IdCount);
            Assert.Equal(Range(0, 1001), uf.Parents.ToArray());
        }

        [Fact]
        public void ResetRestoresSingletonsAndKeepsTheIdCount()
        {
            var uf = new UnionFind();
            for (uint i = 1; i < 50; i++)
                uf.Union(i - 1, i);

            uf.Reset();
            Assert.Equal(50, uf.IdCount);
            Assert.Equal(Range(0, 50), uf.Parents.ToArray());
            for (uint i = 0; i < 50; i++)
                Assert.Equal(i, uf.Find(i));
        }

        [Fact]
        public void PathHalvingPointsEachVisitedNodeAtItsGrandparent()
        {
            // Build the chain 4 -> 3 -> 2 -> 1 -> 0 by linking roots: union by min of 0 with 1 makes 1 -> 0, but the
            // next union finds 1's root, so chains need roots that are linked in decreasing order.
            var uf = new UnionFind();
            uf.Union(3, 4);
            uf.Union(2, 3);
            uf.Union(1, 2);
            uf.Union(0, 1);
            Assert.Equal(new uint[] { 0, 0, 1, 2, 3 }, uf.Parents.ToArray());

            // find(4): 4 -> grand 2, then 2 -> grand 0, then 0 is the root.
            Assert.Equal(0u, uf.Find(4));
            Assert.Equal(new uint[] { 0, 0, 0, 2, 2 }, uf.Parents.ToArray());
        }

        [Fact]
        public void FindNaiveDoesNotCompress()
        {
            var uf = new UnionFind();
            uf.Union(3, 4);
            uf.Union(2, 3);
            uf.Union(1, 2);
            uf.Union(0, 1);
            var before = uf.Parents.ToArray();
            Assert.Equal(0u, uf.FindNaive(4));
            Assert.Equal(before, uf.Parents.ToArray());
        }

        [Fact]
        public void CloneIsIndependent()
        {
            var uf = new UnionFind();
            uf.Union(1, 2);
            var copy = uf.Clone();
            copy.Union(2, 3);
            uf.Union(4, 5);

            Assert.Equal(1u, copy.Find(3));
            Assert.Equal(3u, uf.Find(3));
            Assert.Equal(4u, uf.Find(5));
            Assert.Equal(5u, copy.Find(5));
        }

        [Fact]
        public void IdsBeyondMaxIdAreRejected()
        {
            var uf = new UnionFind();
            Assert.Throws<ArgumentOutOfRangeException>(() => uf.Find(UnionFind.MaxId + 1));
            Assert.Throws<ArgumentOutOfRangeException>(() => uf.Union(0, uint.MaxValue));
            Assert.Throws<ArgumentOutOfRangeException>(() => uf.Reserve(uint.MaxValue));
            Assert.Equal(0, uf.IdCount);
        }

        [Fact]
        public void ConcurrentFindNaiveAgreesWithFind()
        {
            const int n = 20000;
            var random = new Random(7);
            var uf = new UnionFind();
            var reference = new PartitionReference(n);
            for (var i = 0; i < n / 2; i++)
            {
                var a = (uint)random.Next(n);
                var b = (uint)random.Next(n);
                uf.Union(a, b);
                reference.Union(a, b, out _, out _);
            }

            Parallel.For(0, n, i => Assert.Equal(reference.Canonical((uint)i), uf.FindNaive((uint)i)));
        }

        /// <summary>
        /// Random interleavings of every operation, checked after each step against a naive partition (the answers)
        /// and against a transliteration of egglog's code (the exact parent array, so halving matches too).
        /// </summary>
        [Theory]
        [InlineData(1, 16)]
        [InlineData(2, 64)]
        [InlineData(3, 300)]
        [InlineData(4, 1000)]
        public void RandomOperationsMatchTheReferences(int seed, int size)
        {
            var random = new Random(seed);
            var uf = new UnionFind();
            var rust = new RustUnionFindReference();
            var partition = new PartitionReference(size);

            for (var step = 0; step < 4000; step++)
            {
                var x = (uint)random.Next(size);
                var y = (uint)random.Next(size);
                switch (random.Next(100))
                {
                    case < 45:
                        partition.Union(x, y, out var p, out var c);
                        var actual = uf.Union(x, y);
                        Assert.Equal(rust.Union(x, y), actual);
                        Assert.Equal((p, c), actual);
                        break;
                    case < 70:
                        var found = uf.Find(x);
                        Assert.Equal(rust.Find(x), found);
                        Assert.Equal(partition.Canonical(x), found);
                        break;
                    case < 90:
                        var beyond = (uint)random.Next(size + size / 2);
                        Assert.Equal(rust.FindNaive(beyond), uf.FindNaive(beyond));
                        if (beyond < size)
                            Assert.Equal(partition.Canonical(beyond), uf.FindNaive(beyond));
                        break;
                    case < 99:
                        rust.Reserve(x);
                        uf.Reserve(x);
                        break;
                    default:
                        rust.Reset();
                        uf.Reset();
                        partition.Reset();
                        break;
                }

                Assert.Equal(rust.Parents.Count, uf.IdCount);
                Assert.True(rust.Parents.SequenceEqual(uf.Parents.ToArray()));
            }

            var parents = uf.Parents;
            for (var i = 0; i < parents.Length; i++)
                Assert.True(parents[i] <= (uint)i);
        }

    }

}

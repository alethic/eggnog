using System;

using Xunit;

namespace Eggnog.Relations.Tests
{

    public class ReadOnlyUnionFindTests
    {

        [Fact]
        public void ViewIsLiveAndAnswersLikeTheUnionFind()
        {
            var uf = new UnionFind();
            var view = new ReadOnlyUnionFind(uf);
            Assert.Equal(0, view.IdCount);
            Assert.Equal(7u, view.FindNaive(7));

            uf.Union(7, 3);
            uf.Union(9, 7);
            Assert.Equal(10, view.IdCount);
            Assert.Equal(uf.Parents.ToArray(), view.Parents.ToArray());
            for (uint i = 0; i < 12; i++)
                Assert.Equal(uf.FindNaive(i), view.FindNaive(i));

            Assert.Equal(3u, view.FindNaive(9));
            uf.Reset();
            Assert.Equal(9u, view.FindNaive(9));
        }

        [Fact]
        public void ReadingThroughTheViewWritesNothing()
        {
            var uf = new UnionFind();
            uf.Union(3, 4);
            uf.Union(2, 3);
            uf.Union(1, 2);
            var before = uf.Parents.ToArray();
            var view = new ReadOnlyUnionFind(uf);

            Assert.Equal(1u, view.FindNaive(4));
            Assert.Equal(500u, view.FindNaive(500));
            Assert.Equal(before, uf.Parents.ToArray());
            Assert.Equal(5, uf.IdCount);
        }

        [Fact]
        public void DisplacedUnionFindExposesItsUnionFindThroughTheView()
        {
            var d = new DisplacedUnionFind();
            d.Union(4, 2, 1, out _, out _);
            var view = d.UnionFind;
            Assert.Equal(2u, view.FindNaive(4));
            Assert.Equal(5, view.IdCount);

            d.Union(2, 0, 2, out _, out _);
            Assert.Equal(0u, view.FindNaive(4));
        }

        [Fact]
        public void NullIsRejected()
        {
            Assert.Throws<ArgumentNullException>(() => new ReadOnlyUnionFind(null!));
        }

    }

}

using System;
using System.Linq;

using Xunit;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// Tests for <see cref="LayeredUnionFind"/>, the find of a context: <c>find_C = overlay_C ∘ find_P</c> over a root
    /// and two overlay levels against a naive partition, extension without mutation, live levels, and the flattened
    /// form.
    /// </summary>
    public class LayeredUnionFindTests
    {

        [Fact]
        public void TheRootChainIsTheRootsFind()
        {
            var root = new DisplacedUnionFind();
            root.Union(5, 3, 1, out _, out _);
            var chain = new LayeredUnionFind(root);
            Assert.Same(root, chain.Root);
            Assert.Null(chain.Prefix);
            Assert.True(chain.Levels.IsEmpty);
            Assert.Equal(3u, chain.Find(5));
            Assert.Equal(9u, chain.Find(9));
            Assert.Equal(3u, chain.MapAboveRoot(3));
        }

        [Fact]
        public void ExtendAddsALevelAndLeavesTheChainAlone()
        {
            var root = new DisplacedUnionFind();
            var chain = new LayeredUnionFind(root);
            var overlay = new UnionFindOverlay();
            var extended = chain.Extend(overlay);
            Assert.True(chain.Levels.IsEmpty);
            Assert.Equal(1, extended.Levels.Length);
            Assert.Same(overlay, extended.Levels[0]);
            Assert.Same(root, extended.Root);

            // Levels are referenced, so a union made after the extension is seen.
            overlay.Union(8, 2, out _, out _);
            Assert.Equal(2u, extended.Find(8));
            Assert.Equal(8u, chain.Find(8));
        }

        [Theory]
        [InlineData(1)]
        [InlineData(2)]
        [InlineData(3)]
        [InlineData(4)]
        [InlineData(5)]
        public void TwoLevelsAgreeWithANaivePartition(int seed)
        {
            const int Size = 200;
            var random = new Random(seed);
            var root = new DisplacedUnionFind();
            var o1 = new UnionFindOverlay();
            var o2 = new UnionFindOverlay();
            var c0 = new LayeredUnionFind(root);
            var c1 = c0.Extend(o1);
            var c2 = c1.Extend(o2);
            var p0 = new PartitionReference(Size);
            var p1 = new PartitionReference(Size);
            var p2 = new PartitionReference(Size);
            uint ts = 1;

            // Unions at each level, root first so every level is current with its parent when it unions.
            for (var i = 0; i < 60; i++)
            {
                var a = (uint)random.Next(Size);
                var b = (uint)random.Next(Size);
                root.Union(a, b, ts++, out _, out _);
                p0.Union(a, b, out _, out _);
                p1.Union(a, b, out _, out _);
                p2.Union(a, b, out _, out _);
            }

            for (var i = 0; i < 40; i++)
            {
                var a = (uint)random.Next(Size);
                var b = (uint)random.Next(Size);
                o1.Union(c0.Find(a), c0.Find(b), out _, out _);
                p1.Union(a, b, out _, out _);
                p2.Union(a, b, out _, out _);
            }

            for (var i = 0; i < 40; i++)
            {
                var a = (uint)random.Next(Size);
                var b = (uint)random.Next(Size);
                o2.Union(c1.Find(a), c1.Find(b), out _, out _);
                p2.Union(a, b, out _, out _);
            }

            for (uint x = 0; x < Size; x++)
            {
                Assert.Equal(p0.Canonical(x), c0.Find(x));
                Assert.Equal(p1.Canonical(x), c1.Find(x));
                Assert.Equal(p2.Canonical(x), c2.Find(x));
                Assert.Equal(c2.Find(x), c2.MapAboveRoot(root.FindNaive(x)));

                // A child's partition coarsens its parent's: the child's find through its parent's is its own.
                Assert.Equal(c2.Find(x), c2.Find(c1.Find(x)));
            }

            // The flattened form of the two levels gives the same find in one probe above the root.
            var flat = LayeredUnionFind.Flattened(root, FlattenedUnionFind.Build(c2));
            Assert.True(flat.Levels.IsEmpty);
            for (uint x = 0; x < Size; x++)
                Assert.Equal(c2.Find(x), flat.Find(x));

            // A level below the flattened prefix composes with it.
            var o3 = new UnionFindOverlay();
            var c3 = flat.Extend(o3);
            var p3 = new PartitionReference(Size);
            for (uint x = 0; x < Size; x++)
                p3.Union(x, p2.Canonical(x), out _, out _);

            for (var i = 0; i < 30; i++)
            {
                var a = (uint)random.Next(Size);
                var b = (uint)random.Next(Size);
                o3.Union(flat.Find(a), flat.Find(b), out _, out _);
                p3.Union(a, b, out _, out _);
            }

            for (uint x = 0; x < Size; x++)
                Assert.Equal(p3.Canonical(x), c3.Find(x));

            var flatter = LayeredUnionFind.Flattened(root, FlattenedUnionFind.Build(c3));
            for (uint x = 0; x < Size; x++)
                Assert.Equal(c3.Find(x), flatter.Find(x));
        }

        [Fact]
        public void EmptyLevelsAreSkipped()
        {
            var root = new DisplacedUnionFind();
            root.Union(4, 1, 1, out _, out _);
            var chain = new LayeredUnionFind(root).Extend(new UnionFindOverlay()).Extend(new UnionFindOverlay());
            Assert.All(chain.Levels.ToArray(), level => Assert.True(level.IsEmpty));
            Assert.Equal(1u, chain.Find(4));
            Assert.Equal(7u, chain.Find(7));
        }

    }

}

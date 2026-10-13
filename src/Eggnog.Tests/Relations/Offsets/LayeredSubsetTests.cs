using System;
using System.Collections.Generic;
using System.Linq;

using Xunit;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// Tests for <see cref="LayeredSubset"/> and <see cref="SubsetArrayPool"/>: a property test over 2000 seeds
    /// against a reference of one <see cref="SortedSet{T}"/> per layer, plus the representation rules (single versus
    /// multi-layer, collapsing, the root path), ownership and pooling.
    /// </summary>
    [Collection(AllocationProbeCollection.Name)]
    public class LayeredSubsetTests
    {

        /// <summary>
        /// The number of seeds of the property test.
        /// </summary>
        const int Seeds = 2000;

        /// <summary>
        /// Draws a layer count: mostly shallow chains, sometimes up to the limit.
        /// </summary>
        static int DrawLayerCount(Random random) => random.Next(8) switch
        {
            0 => LayeredSubset.MaxLayers,
            1 => random.Next(1, LayeredSubset.MaxLayers + 1),
            _ => random.Next(1, 6),
        };

        /// <summary>
        /// Draws the rows of one layer and a subset holding them: absent, empty dense, empty sparse, dense, or sparse.
        /// </summary>
        static Subset DrawLayer(Random random, SortedSet<int> rows)
        {
            switch (random.Next(6))
            {
                case 0:
                    return Subset.Empty;
                case 1:
                    var at = random.Next(100);
                    return Subset.Dense(at, at);
                case 2:
                    return OffsetTestUtil.Sparse(Array.Empty<int>());
                case 3:
                {
                    var start = random.Next(200);
                    var end = start + random.Next(1, 120);
                    rows.UnionWith(Enumerable.Range(start, end - start));
                    return Subset.Dense(start, end);
                }
                default:
                {
                    var drawn = OffsetTestUtil.RandomSorted(random, random.Next(1, 60), 300, duplicates: false);
                    rows.UnionWith(drawn);
                    return random.Next(2) == 0 ? OffsetTestUtil.Sparse(drawn) : OffsetTestUtil.Build(drawn);
                }
            }
        }

        /// <summary>
        /// Draws a layered subset over <paramref name="layerCount"/> layers and its reference, built by
        /// <see cref="LayeredSubset.FromLayers"/> or, when at most one layer has rows, sometimes by
        /// <see cref="LayeredSubset.Single"/>.
        /// </summary>
        static LayeredSubset Draw(Random random, int layerCount, SubsetArrayPool? pool, out SortedSet<int>[] reference)
        {
            reference = EmptyReference();

            // Bias toward few nonempty layers, the common case, so collapses happen often.
            var density = random.Next(4);
            var array = pool is not null && pool.LayerCount >= layerCount && random.Next(2) == 0 ? pool.Rent() : new Subset[layerCount];
            for (var l = 0; l < layerCount; l++)
            {
                if (density == 0 && random.Next(layerCount) != 0)
                    continue;

                array[l] = DrawLayer(random, reference[l]);
            }

            var drawn = reference;
            var nonempty = Enumerable.Range(0, layerCount).Where(l => drawn[l].Count > 0).ToArray();
            if (nonempty.Length <= 1 && random.Next(2) == 0)
            {
                var layer = nonempty.Length == 1 ? nonempty[0] : random.Next(layerCount);
                var rows = array[layer].Take();
                for (var l = 0; l < array.Length; l++)
                    array[l].Dispose();

                pool?.Return(array);
                return LayeredSubset.Single(layer, rows);
            }

            return LayeredSubset.FromLayers(array, pool);
        }

        /// <summary>
        /// Checks every observable member of <paramref name="subset"/> against <paramref name="reference"/>.
        /// </summary>
        static void Check(in LayeredSubset subset, SortedSet<int>[] reference, string context)
        {
            var mask = 0u;
            var size = 0;
            for (var l = 0; l < LayeredSubset.MaxLayers; l++)
            {
                var rows = OffsetTestUtil.Collect(subset.GetLayer(l));
                Assert.True(reference[l].SequenceEqual(rows), $"{context}: layer {l} holds [{string.Join(",", rows)}], expected [{string.Join(",", reference[l])}] ({subset})");
                if (rows.Count > 0)
                    mask |= 1u << l;

                size += rows.Count;
            }

            Assert.Equal(mask, subset.LayerMask);
            Assert.Equal(size, subset.Size);
            Assert.Equal(size == 0, subset.IsEmpty);
            var multi = (mask & (mask - 1)) != 0;
            Assert.Equal(multi, subset.IsMultiLayer);
            if (multi)
            {
                Assert.Equal(-1, subset.SingleLayer);
                var copy = subset;
                Assert.Throws<InvalidOperationException>(() => copy.SingleRows);
            }
            else
            {
                if (mask != 0)
                    Assert.Equal(System.Numerics.BitOperations.TrailingZeroCount(mask), subset.SingleLayer);

                Assert.Equal(reference[subset.SingleLayer], OffsetTestUtil.Collect(subset.SingleRows));
            }
        }

        /// <summary>
        /// Gets the reference of an empty subset.
        /// </summary>
        static SortedSet<int>[] EmptyReference() => Enumerable.Range(0, LayeredSubset.MaxLayers).Select(_ => new SortedSet<int>()).ToArray();

        /// <summary>
        /// Copies a reference.
        /// </summary>
        static SortedSet<int>[] Copy(SortedSet<int>[] reference) => reference.Select(s => new SortedSet<int>(s)).ToArray();

        [Fact]
        public void LayeredSubsetsAgreeWithPerLayerSets()
        {
            for (var seed = 0; seed < Seeds; seed++)
            {
                var random = new Random(seed);
                var layerCount = DrawLayerCount(random);
                var pool = random.Next(3) == 0 ? null : new SubsetArrayPool(random.Next(2) == 0 ? layerCount : random.Next(1, LayeredSubset.MaxLayers + 1));
                var context = $"seed {seed}";

                var a = Draw(random, layerCount, pool, out var ra);
                Check(a, ra, context + " (built)");

                // An independent copy: changing or disposing it leaves the original alone.
                var clone = a.Clone(pool);
                Check(clone, ra, context + " (clone)");
                var shrink = LayeredSubset.Single(random.Next(layerCount), Subset.Empty);
                clone.Intersect(in shrink, pool);
                Check(a, ra, context + " (after the clone changed)");
                clone.Dispose(pool);
                shrink.Dispose(pool);
                Check(a, ra, context + " (after the clone was disposed)");

                // Intersections, chained: per layer, only rows both sides hold in that layer survive.
                for (var step = 0; step < 3; step++)
                {
                    var b = Draw(random, random.Next(2) == 0 ? layerCount : DrawLayerCount(random), pool, out var rb);
                    Check(b, rb, $"{context} step {step} (other)");
                    var expected = Copy(ra);
                    for (var l = 0; l < expected.Length; l++)
                        expected[l].IntersectWith(rb[l]);

                    a.Intersect(in b, pool);
                    Check(a, expected, $"{context} step {step} (intersection)");
                    Check(b, rb, $"{context} step {step} (other after)");
                    b.Dispose(pool);
                    ra = expected;
                }

                // Moving out leaves Empty behind.
                var taken = a.Take();
                Check(a, EmptyReference(), context + " (taken from)");
                Check(taken, ra, context + " (taken)");
                taken.Dispose(pool);
                Assert.True(taken.IsEmpty);
                taken.Dispose(pool);

                if (pool is not null)
                    Assert.InRange(pool.Count, 0, SubsetArrayPool.MaxRetained);
            }
        }

        [Fact]
        public void EmptyIsTheDefault()
        {
            var empty = LayeredSubset.Empty;
            Assert.True(empty.IsEmpty);
            Assert.False(empty.IsMultiLayer);
            Assert.Equal(0, empty.Size);
            Assert.Equal(0u, empty.LayerMask);
            Assert.Equal(0, empty.SingleLayer);
            Assert.True(empty.SingleRows.IsEmpty);
            Assert.True(default(LayeredSubset).GetLayer(31).IsEmpty);
            empty.Dispose(null);
        }

        [Fact]
        public void LayersOutsideTheLimitAreRefused()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => LayeredSubset.Single(-1, Subset.Empty));
            Assert.Throws<ArgumentOutOfRangeException>(() => LayeredSubset.Single(LayeredSubset.MaxLayers, Subset.Empty));
            Assert.Throws<ArgumentOutOfRangeException>(() => LayeredSubset.Empty.GetLayer(LayeredSubset.MaxLayers));
            Assert.Throws<ArgumentOutOfRangeException>(() => LayeredSubset.Empty.GetLayer(-1));
            Assert.Throws<ArgumentException>(() => LayeredSubset.FromLayers(new Subset[LayeredSubset.MaxLayers + 1], null));
            Assert.Throws<ArgumentNullException>(() => LayeredSubset.FromLayers(null!, null));

            // A layer at the top of the mask works.
            var top = LayeredSubset.Single(31, Subset.Dense(4, 6));
            Assert.Equal(1u << 31, top.LayerMask);
            Assert.Equal(new[] { 4, 5 }, OffsetTestUtil.Collect(top.GetLayer(31)));
        }

        [Fact]
        public void TwoSinglesOfOneLayerIntersectAsSubsetsDo()
        {
            // The root path: the result is exactly Subset.Intersect's, representation included.
            var random = new Random(3);
            for (var trial = 0; trial < 300; trial++)
            {
                var left = OffsetTestUtil.RandomSorted(random, random.Next(0, 40), 120, duplicates: random.Next(2) == 0);
                var right = OffsetTestUtil.RandomSorted(random, random.Next(0, 40), 120, duplicates: random.Next(2) == 0);
                var plain = random.Next(2) == 0 ? OffsetTestUtil.Build(left) : Subset.Dense(10, 60 + trial % 40);
                var other = random.Next(2) == 0 ? OffsetTestUtil.Build(right) : Subset.Dense(trial % 50, 90);
                var layered = LayeredSubset.Single(0, plain.Clone());
                var otherLayered = LayeredSubset.Single(0, other.Clone());

                plain.Intersect(other.AsRef());
                layered.Intersect(in otherLayered, null);
                Assert.False(layered.IsMultiLayer);
                Assert.Equal(0, layered.SingleLayer);
                Assert.Equal(plain.AsRef().IsDense, layered.SingleRows.IsDense);
                Assert.Equal(OffsetTestUtil.Collect(plain), OffsetTestUtil.Collect(layered.SingleRows));

                plain.Dispose();
                other.Dispose();
                layered.Dispose(null);
                otherLayered.Dispose(null);
            }
        }

        [Fact]
        public void SinglesOfDifferentLayersAreDisjoint()
        {
            var a = LayeredSubset.Single(1, Subset.Dense(0, 10));
            var b = LayeredSubset.Single(2, Subset.Dense(0, 10));
            a.Intersect(in b, null);
            Assert.True(a.IsEmpty);
            Assert.Equal(0u, a.LayerMask);
            a.Dispose(null);
            b.Dispose(null);
        }

        [Fact]
        public void FromLayersCollapsesAndReturnsTheArray()
        {
            var pool = new SubsetArrayPool(4);

            // Nothing nonempty: Empty, and empty sparse entries are released.
            var none = pool.Rent();
            none[1] = OffsetTestUtil.Sparse(Array.Empty<int>());
            none[2] = Subset.Dense(7, 7);
            var empty = LayeredSubset.FromLayers(none, pool);
            Assert.True(empty.IsEmpty);
            Assert.False(empty.IsMultiLayer);
            Assert.Equal(1, pool.Count);

            // One nonempty layer: single, in that layer.
            var one = pool.Rent();
            Assert.Equal(0, pool.Count);
            Assert.All(one, s => Assert.True(s.IsEmpty));
            one[3] = OffsetTestUtil.Sparse(new[] { 2, 9 });
            var single = LayeredSubset.FromLayers(one, pool);
            Assert.False(single.IsMultiLayer);
            Assert.Equal(3, single.SingleLayer);
            Assert.Equal(new[] { 2, 9 }, OffsetTestUtil.Collect(single.SingleRows));
            Assert.Equal(1, pool.Count);

            // Two nonempty layers: multi-layer, keeping the array.
            var two = pool.Rent();
            two[0] = Subset.Dense(0, 3);
            two[2] = Subset.Dense(5, 6);
            var multi = LayeredSubset.FromLayers(two, pool);
            Assert.True(multi.IsMultiLayer);
            Assert.Equal(0b101u, multi.LayerMask);
            Assert.Equal(4, multi.Size);
            Assert.Equal(0, pool.Count);

            // Intersecting down to one layer collapses and gives the array back.
            var keep = LayeredSubset.Single(2, Subset.Dense(0, 100));
            multi.Intersect(in keep, pool);
            Assert.False(multi.IsMultiLayer);
            Assert.Equal(2, multi.SingleLayer);
            Assert.Equal(new[] { 5 }, OffsetTestUtil.Collect(multi.SingleRows));
            Assert.Equal(1, pool.Count);

            single.Dispose(pool);
            multi.Dispose(pool);
            keep.Dispose(pool);
            empty.Dispose(pool);
        }

        [Fact]
        public void PoolsHandOutClearedArraysOfTheirLength()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new SubsetArrayPool(0));
            Assert.Throws<ArgumentOutOfRangeException>(() => new SubsetArrayPool(LayeredSubset.MaxLayers + 1));
            Assert.Throws<ArgumentNullException>(() => new SubsetArrayPool(2).Return(null!));

            var pool = new SubsetArrayPool(3);
            Assert.Equal(3, pool.LayerCount);
            var array = pool.Rent();
            Assert.Equal(3, array.Length);
            array[1] = Subset.Dense(1, 4);
            pool.Return(array);
            Assert.Equal(1, pool.Count);
            var again = pool.Rent();
            Assert.Same(array, again);
            Assert.All(again, s => Assert.Equal(Subset.Empty, s));

            // Another length is dropped, not kept.
            pool.Return(new Subset[5]);
            Assert.Equal(0, pool.Count);

            // At most MaxRetained arrays are kept.
            for (var i = 0; i < SubsetArrayPool.MaxRetained + 10; i++)
                pool.Return(new Subset[3]);

            Assert.Equal(SubsetArrayPool.MaxRetained, pool.Count);
        }

        [Fact]
        public void ACloneOfAMultiLayerSubsetUsesThePoolWhenItsArraysAreLongEnough()
        {
            var array = new Subset[6];
            array[1] = Subset.Dense(0, 2);
            array[5] = OffsetTestUtil.Sparse(new[] { 3, 8 });
            var multi = LayeredSubset.FromLayers(array, null);

            // Too short a pool: a fresh array; long enough: the pool's.
            var shortPool = new SubsetArrayPool(4);
            var copy = multi.Clone(shortPool);
            Assert.Equal(0b100010u, copy.LayerMask);
            copy.Dispose(shortPool);
            Assert.Equal(0, shortPool.Count);

            var longPool = new SubsetArrayPool(8);
            var pooled = longPool.Rent();
            longPool.Return(pooled);
            var copy2 = multi.Clone(longPool);
            Assert.Equal(0, longPool.Count);
            Assert.Equal(new[] { 3, 8 }, OffsetTestUtil.Collect(copy2.GetLayer(5)));
            copy2.Dispose(longPool);
            Assert.Equal(1, longPool.Count);
            multi.Dispose(null);
        }

        [Fact]
        public void WarmMultiLayerOperationsAllocateNothing()
        {
            var pool = new SubsetArrayPool(4);
            var left = Enumerable.Range(0, 300).Select(x => x * 3).ToArray();
            var right = Enumerable.Range(0, 300).Select(x => x * 2).ToArray();

            void Body()
            {
                var a = pool.Rent();
                a[0] = Subset.Dense(0, 500);
                a[1] = SubsetRef.Sparse(left, 0, left.Length).ToOwned();
                a[3] = Subset.Dense(10, 20);
                var x = LayeredSubset.FromLayers(a, pool);

                var b = pool.Rent();
                b[1] = SubsetRef.Sparse(right, 0, right.Length).ToOwned();
                b[3] = Subset.Dense(0, 15);
                var y = LayeredSubset.FromLayers(b, pool);

                var copy = x.Clone(pool);
                x.Intersect(in y, pool);
                _ = x.Size + copy.Size + (int)x.LayerMask;
                copy.Dispose(pool);
                x.Dispose(pool);
                y.Dispose(pool);
            }

            for (var i = 0; i < 5; i++)
                Body();

            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var i = 0; i < 200; i++)
                Body();

            Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
        }

    }

}

using System;
using System.Collections.Generic;
using System.Linq;

using Xunit;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// Tests for <see cref="LayeredSubset"/> and <see cref="SubsetArrayPool"/>: a property test over 2000 seeds
    /// against a reference of one <see cref="SortedSet{T}"/> per layer, over chains shallow, around the mask's 64 layers
    /// and well past them, plus the representation rules (single versus multi-layer, collapsing, the root path, the
    /// layers past the mask), ownership and pooling.
    /// </summary>
    [Collection(AllocationProbeCollection.Name)]
    public class LayeredSubsetTests
    {

        /// <summary>
        /// The number of seeds of the property test.
        /// </summary>
        const int Seeds = 2000;

        /// <summary>
        /// The most layers a drawn subset spans: well past <see cref="LayeredSubset.MaskedLayers"/>.
        /// </summary>
        const int MaxDrawnLayers = 150;

        /// <summary>
        /// Draws a layer count: mostly shallow chains, sometimes about 32 or 64 layers, sometimes up to
        /// <see cref="MaxDrawnLayers"/>.
        /// </summary>
        static int DrawLayerCount(Random random) => random.Next(10) switch
        {
            0 => MaxDrawnLayers,
            1 => random.Next(1, MaxDrawnLayers + 1),
            2 => random.Next(LayeredSubset.MaskedLayers - 3, LayeredSubset.MaskedLayers + 4),
            3 => random.Next(30, 35),
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
            var nonempty = new List<int>();
            var size = 0;
            for (var l = 0; l < MaxDrawnLayers + 2; l++)
            {
                var rows = OffsetTestUtil.Collect(subset.GetLayer(l));
                var expected = l < reference.Length ? reference[l] : new SortedSet<int>();
                Assert.True(expected.SequenceEqual(rows), $"{context}: layer {l} holds [{string.Join(",", rows)}], expected [{string.Join(",", expected)}] ({subset})");
                if (rows.Count > 0)
                    nonempty.Add(l);

                size += rows.Count;
            }

            // The layers come in ascending order, each once.
            Assert.Equal(nonempty, LayerTestUtil.Layers(subset));
            Assert.Equal(nonempty.Count == 0 ? 0 : nonempty[^1] + 1, subset.TopLayer);
            Assert.Equal(size, subset.Size);
            Assert.Equal(size == 0, subset.IsEmpty);
            var multi = nonempty.Count >= 2;
            Assert.Equal(multi, subset.IsMultiLayer);
            if (multi)
            {
                Assert.Equal(-1, subset.SingleLayer);
                var copy = subset;
                Assert.Throws<InvalidOperationException>(() => copy.SingleRows);
            }
            else
            {
                if (nonempty.Count == 1)
                    Assert.Equal(nonempty[0], subset.SingleLayer);

                Assert.Equal(reference[subset.SingleLayer], OffsetTestUtil.Collect(subset.SingleRows));
            }
        }

        /// <summary>
        /// Gets the reference of an empty subset.
        /// </summary>
        static SortedSet<int>[] EmptyReference() => Enumerable.Range(0, MaxDrawnLayers).Select(_ => new SortedSet<int>()).ToArray();

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
                var pool = random.Next(3) == 0 ? null : new SubsetArrayPool(random.Next(2) == 0 ? layerCount : random.Next(1, MaxDrawnLayers + 1));
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
            Assert.Empty(LayerTestUtil.Layers(empty));
            Assert.Equal(0, empty.TopLayer);
            Assert.Equal(0, empty.SingleLayer);
            Assert.True(empty.SingleRows.IsEmpty);
            Assert.True(default(LayeredSubset).GetLayer(31).IsEmpty);
            Assert.True(default(LayeredSubset).GetLayer(1000).IsEmpty);
            empty.Dispose(null);
        }

        [Fact]
        public void OnlyNegativeLayersAreRefused()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => LayeredSubset.Single(-1, Subset.Empty));
            Assert.Throws<ArgumentOutOfRangeException>(() => LayeredSubset.Empty.GetLayer(-1));
            Assert.Throws<ArgumentNullException>(() => LayeredSubset.FromLayers(null!, null));

            // Layers at the top of a 32-bit and of the 64-bit mask, and past both, work.
            foreach (var layer in new[] { 31, 32, 63, 64, 65, 1000 })
            {
                var single = LayeredSubset.Single(layer, Subset.Dense(4, 6));
                Assert.Equal(new[] { layer }, LayerTestUtil.Layers(single));
                Assert.Equal(layer + 1, single.TopLayer);
                Assert.Equal(new[] { 4, 5 }, OffsetTestUtil.Collect(single.GetLayer(layer)));
                Assert.True(single.GetLayer(layer + 1).IsEmpty);
                Assert.Equal(layer, single.SingleLayer);
                single.Dispose(null);
            }

            // An array of any length is taken.
            var wide = new Subset[300];
            wide[7] = Subset.Dense(0, 1);
            wide[299] = Subset.Dense(2, 3);
            var multi = LayeredSubset.FromLayers(wide, null);
            Assert.Equal(new[] { 7, 299 }, LayerTestUtil.Layers(multi));
            multi.Dispose(null);
        }

        [Fact]
        public void LayersPastTheMaskAreFoundByTheirEntries()
        {
            const int Count = 150;
            var pool = new SubsetArrayPool(Count);
            var array = pool.Rent();
            array[3] = Subset.Dense(0, 4);
            array[40] = OffsetTestUtil.Sparse(new[] { 1, 5, 9 });
            array[63] = Subset.Dense(2, 3);
            array[64] = Subset.Dense(7, 9);
            array[100] = OffsetTestUtil.Sparse(new[] { 4, 6 });
            array[149] = Subset.Dense(0, 1);
            array[120] = OffsetTestUtil.Sparse(Array.Empty<int>());
            var subset = LayeredSubset.FromLayers(array, pool);
            Assert.True(subset.IsMultiLayer);
            Assert.Equal(new[] { 3, 40, 63, 64, 100, 149 }, LayerTestUtil.Layers(subset));
            Assert.Equal(150, subset.TopLayer);
            Assert.Equal(4 + 3 + 1 + 2 + 2 + 1, subset.Size);
            Assert.True(subset.GetLayer(120).IsEmpty);
            var text = subset.ToString();
            Assert.StartsWith("{L3:", text);
            Assert.Contains(", L64:", text);
            Assert.Contains(", L149:", text);

            // A clone takes a pooled array, as the pool's arrays are long enough.
            var spare = pool.Rent();
            pool.Return(spare);
            var clone = subset.Clone(pool);
            Assert.Equal(0, pool.Count);
            Assert.Equal(LayerTestUtil.Layers(subset), LayerTestUtil.Layers(clone));
            Assert.Equal(new[] { 4, 6 }, OffsetTestUtil.Collect(clone.GetLayer(100)));

            // Intersecting with a multi-layer subset keeps the layers both hold: one masked layer and one past the mask
            // stay multi-layer.
            var other = new Subset[Count];
            other[40] = Subset.Dense(0, 6);
            other[100] = Subset.Dense(0, 100);
            other[130] = Subset.Dense(0, 100);
            var both = LayeredSubset.FromLayers(other, null);
            subset.Intersect(in both, pool);
            Assert.True(subset.IsMultiLayer);
            Assert.Equal(new[] { 40, 100 }, LayerTestUtil.Layers(subset));
            Assert.Equal(new[] { 1, 5 }, OffsetTestUtil.Collect(subset.GetLayer(40)));
            Assert.Equal(101, subset.TopLayer);

            // Down to one layer past the mask, it collapses and gives its array back.
            var high = LayeredSubset.Single(100, Subset.Dense(0, 5));
            subset.Intersect(in high, pool);
            Assert.False(subset.IsMultiLayer);
            Assert.Equal(100, subset.SingleLayer);
            Assert.Equal(new[] { 4 }, OffsetTestUtil.Collect(subset.SingleRows));
            Assert.Equal(1, pool.Count);

            // Two layers past the mask and none in it stay multi-layer; emptying one collapses to the other.
            var twoHigh = new Subset[Count];
            twoHigh[70] = Subset.Dense(0, 3);
            twoHigh[90] = Subset.Dense(0, 3);
            var pair = LayeredSubset.FromLayers(twoHigh, null);
            Assert.True(pair.IsMultiLayer);
            Assert.Equal(new[] { 70, 90 }, LayerTestUtil.Layers(pair));
            var keep90 = new Subset[Count];
            keep90[90] = Subset.Dense(1, 2);
            keep90[3] = Subset.Dense(0, 9);
            var filter = LayeredSubset.FromLayers(keep90, null);
            pair.Intersect(in filter, null);
            Assert.False(pair.IsMultiLayer);
            Assert.Equal(90, pair.SingleLayer);
            Assert.Equal(new[] { 1 }, OffsetTestUtil.Collect(pair.SingleRows));

            // A single-layer subset past the mask intersects with a multi-layer one by its layer.
            var lone = LayeredSubset.Single(149, Subset.Dense(0, 10));
            lone.Intersect(in clone, pool);
            Assert.Equal(149, lone.SingleLayer);
            Assert.Equal(new[] { 0 }, OffsetTestUtil.Collect(lone.SingleRows));

            // Disposing the clone gives its array back, cleared.
            clone.Dispose(pool);
            Assert.Equal(2, pool.Count);
            Assert.All(pool.Rent(), s => Assert.Equal(Subset.Empty, s));

            subset.Dispose(pool);
            both.Dispose(null);
            high.Dispose(null);
            pair.Dispose(null);
            filter.Dispose(null);
            lone.Dispose(null);
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
            Assert.Empty(LayerTestUtil.Layers(a));
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
            Assert.Equal(0b101u, LayerTestUtil.Mask(multi));
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
            Assert.Equal(1000, new SubsetArrayPool(1000).Rent().Length);
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
            Assert.Equal(0b100010u, LayerTestUtil.Mask(copy));
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

        /// <summary>
        /// Building, copying, intersecting, walking and disposing multi-layer subsets from a warm pool allocates nothing:
        /// over 4 layers, over 64 (the whole mask) and over 130 (past it).
        /// </summary>
        [Theory]
        [InlineData(4, 1, 3)]
        [InlineData(64, 33, 63)]
        [InlineData(130, 64, 129)]
        public void WarmMultiLayerOperationsAllocateNothing(int layerCount, int middle, int last)
        {
            var pool = new SubsetArrayPool(layerCount);
            var left = Enumerable.Range(0, 300).Select(x => x * 3).ToArray();
            var right = Enumerable.Range(0, 300).Select(x => x * 2).ToArray();

            void Body()
            {
                var a = pool.Rent();
                a[0] = Subset.Dense(0, 500);
                a[middle] = SubsetRef.Sparse(left, 0, left.Length).ToOwned();
                a[last] = Subset.Dense(10, 20);
                var x = LayeredSubset.FromLayers(a, pool);

                var b = pool.Rent();
                b[middle] = SubsetRef.Sparse(right, 0, right.Length).ToOwned();
                b[last] = Subset.Dense(0, 15);
                var y = LayeredSubset.FromLayers(b, pool);

                var copy = x.Clone(pool);
                x.Intersect(in y, pool);
                var walked = 0;
                foreach (var layer in copy.GetLayers())
                    walked += layer;

                _ = x.Size + copy.Size + x.TopLayer + walked;
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

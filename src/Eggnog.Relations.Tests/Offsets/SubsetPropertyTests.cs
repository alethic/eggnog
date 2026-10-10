using System;
using System.Collections.Generic;
using System.Linq;

using Xunit;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// Randomized tests of <see cref="Subset"/> and <see cref="SubsetRef"/> against <see cref="SubsetModel"/>, a
    /// plain restatement of egglog's semantics, kinds included.
    /// </summary>
    public class SubsetPropertyTests
    {

        [Theory]
        [InlineData(1)]
        [InlineData(2)]
        [InlineData(3)]
        [InlineData(4)]
        public void IntersectMatchesModel(int seed)
        {
            var random = new Random(seed);
            for (var trial = 0; trial < 1500; trial++)
            {
                var a = SubsetModel.Random(random);
                var b = SubsetModel.Random(random);
                var expected = a.Intersect(b);

                var actual = a.Realize(random);
                var other = b.Realize(random);
                actual.Intersect(other.AsRef());
                expected.AssertMatches(actual, $"{a} ∩ {b}");
                b.AssertMatches(other, "intersect must not change its argument");

                var actual2 = a.Realize(random);
                actual2.Intersect(b.RealizeRef(random));
                expected.AssertMatches(actual2, $"{a} ∩ ref {b}");

                actual.Dispose();
                actual2.Dispose();
                other.Dispose();
            }
        }

        [Theory]
        [InlineData(11)]
        [InlineData(12)]
        public void RetainMatchesModel(int seed)
        {
            var random = new Random(seed);
            for (var trial = 0; trial < 1500; trial++)
            {
                var a = SubsetModel.Random(random);
                Func<int, bool> keep = random.Next(4) switch
                {
                    0 => _ => true,
                    1 => _ => false,
                    2 => CutAt(random.Next(0, 500), random.Next(0, 500)),
                    _ => Modulus(random.Next(1, 6), random.Next(0, 6)),
                };

                var actual = a.Realize(random);
                var filter = new PredicateFilter(keep);
                actual.Retain(ref filter);
                Assert.Equal(a.Contents, filter.Seen);
                a.Retain(keep).AssertMatches(actual, $"retain {a}");
                actual.Dispose();
            }
        }

        [Theory]
        [InlineData(21)]
        [InlineData(22)]
        public void AddRowSortedMatchesModel(int seed)
        {
            var random = new Random(seed);
            for (var trial = 0; trial < 300; trial++)
            {
                var model = SubsetModel.Random(random);
                var actual = model.Realize(random);
                for (var step = 0; step < 40; step++)
                {
                    var empty = model.IsDense ? model.End == model.Start : model.Rows.Count == 0;
                    var last = empty ? random.Next(0, 50) : model.IsDense ? model.End - 1 : model.Rows[^1];
                    var row = random.Next(5) switch
                    {
                        0 => last,
                        1 => last + 1,
                        2 => Math.Max(0, last - 1),
                        _ => last + random.Next(1, 10),
                    };

                    var next = model.AddRowSorted(row);
                    if (next is null)
                    {
                        var threw = false;
                        try
                        {
                            actual.AddRowSorted(row);
                        }
                        catch (InvalidOperationException)
                        {
                            threw = true;
                        }

                        Assert.True(threw, $"adding {row} to {model} should throw");
                    }
                    else
                    {
                        actual.AddRowSorted(row);
                        model = next;
                    }

                    if (step % 8 == 7 || model.Contents.Count < 40)
                        model.AssertMatches(actual, $"after adding {row}");
                }

                model.AssertMatches(actual, "at the end");
                actual.Dispose();
            }
        }

        [Theory]
        [InlineData(31)]
        [InlineData(32)]
        public void ChunkedScansVisitEveryRowOnce(int seed)
        {
            var random = new Random(seed);
            for (var trial = 0; trial < 1000; trial++)
            {
                var model = SubsetModel.Random(random);
                var subset = model.Realize(random);
                var chunk = random.Next(1, 64);
                var visitor = new CollectingVisitor();
                var position = 0;
                var calls = 0;
                while (true)
                {
                    calls++;
                    var next = subset.AsRef().IterBounded(position, position + chunk, ref visitor);
                    if (next < 0)
                        break;

                    Assert.Equal(position + chunk, next);
                    position = next;
                }

                Assert.Equal(model.Contents, visitor.Rows);
                Assert.Equal(Math.Max(1, (model.Contents.Count + chunk - 1) / chunk), calls);
                subset.Dispose();
            }
        }

        [Theory]
        [InlineData(41)]
        [InlineData(42)]
        public void TryIntersectDenseMatchesClip(int seed)
        {
            var random = new Random(seed);
            for (var trial = 0; trial < 1500; trial++)
            {
                var model = SubsetModel.Random(random);
                var start = random.Next(0, 3000);
                var range = new OffsetRange(start, start + random.Next(0, 800));
                var source = model.RealizeRef(random);
                var expected = OffsetTestUtil.Clip(model.Contents, range.Start, range.End);
                var nonempty = source.TryIntersectDense(range, out var result);
                Assert.Equal(expected.Count > 0, nonempty);
                if (nonempty)
                {
                    Assert.Equal(source.IsDense, result.IsDense);
                    Assert.Equal(expected, OffsetTestUtil.Collect(result));
                }

                // The owned intersection agrees with the borrowed one whenever the borrowed one is nonempty.
                var owned = model.Realize(random);
                owned.Intersect(SubsetRef.Dense(range));
                Assert.Equal(expected, OffsetTestUtil.Collect(owned));
                owned.Dispose();
            }
        }

        [Theory]
        [InlineData(51)]
        [InlineData(52)]
        [InlineData(53)]
        public void RandomProgramsKeepOwnershipConsistent(int seed)
        {
            // Several live subsets go through random sequences of every mutating operation, including clones, moves
            // and disposals; a buffer returned twice or shared by mistake would surface as a model mismatch.
            var random = new Random(seed);
            const int slots = 5;
            var subsets = new Subset[slots];
            var models = new SubsetModel[slots];
            for (var i = 0; i < slots; i++)
            {
                models[i] = SubsetModel.Random(random);
                subsets[i] = models[i].Realize(random);
            }

            for (var step = 0; step < 4000; step++)
            {
                var i = random.Next(slots);
                var j = (i + 1 + random.Next(slots - 1)) % slots;
                string op;
                switch (random.Next(8))
                {
                    case 0:
                        op = $"intersect {models[i]} with slot {models[j]}";
                        subsets[i].Intersect(subsets[j].AsRef());
                        models[i] = models[i].Intersect(models[j]);
                        break;
                    case 1:
                        var fresh = SubsetModel.Random(random);
                        op = $"intersect {models[i]} with {fresh}";
                        subsets[i].Intersect(fresh.RealizeRef(random));
                        models[i] = models[i].Intersect(fresh);
                        break;
                    case 2:
                        var modulus = random.Next(1, 4);
                        op = $"retain {models[i]} mod {modulus}";
                        var filter = new PredicateFilter(r => r % modulus == 0);
                        subsets[i].Retain(ref filter);
                        models[i] = models[i].Retain(r => r % modulus == 0);
                        break;
                    case 3:
                        var contents = models[i].Contents;
                        var row = (contents.Count == 0 ? 0 : contents[^1]) + random.Next(0, 3);
                        op = $"add {row} to {models[i]}";
                        subsets[i].AddRowSorted(row);
                        models[i] = models[i].AddRowSorted(row)!;
                        break;
                    case 4:
                        op = $"clone slot {j} into slot {i}";
                        subsets[i].Dispose();
                        subsets[i] = subsets[j].Clone();
                        models[i] = models[j];
                        break;
                    case 5:
                        op = $"move slot {j} into slot {i}";
                        subsets[i].Dispose();
                        subsets[i] = subsets[j].Take();
                        models[i] = models[j];
                        models[j] = SubsetModel.Empty;
                        break;
                    case 6:
                        op = $"dispose slot {i}";
                        subsets[i].Dispose();
                        models[i] = SubsetModel.Empty;
                        break;
                    default:
                        op = $"replace slot {i}";
                        subsets[i].Dispose();
                        models[i] = SubsetModel.Random(random);
                        subsets[i] = models[i].Realize(random);
                        break;
                }

                for (var k = 0; k < slots; k++)
                    models[k].AssertMatches(subsets[k], $"step {step} ({op}), slot {k}");
            }

            for (var i = 0; i < slots; i++)
                subsets[i].Dispose();
        }

        [Fact]
        public void SparseSparseStrategyBoundariesMatchModel()
        {
            // Lengths straddling the 16-element and 4x thresholds of the strategy choice (mod.rs:447-448).
            var random = new Random(77);
            var lengths = new[] { 0, 1, 3, 4, 5, 15, 16, 17, 63, 64, 65, 68, 256, 257 };
            foreach (var n in lengths)
            {
                foreach (var m in lengths)
                {
                    for (var rep = 0; rep < 3; rep++)
                    {
                        var a = SubsetModel.Sparse(OffsetTestUtil.RandomSorted(random, n, 600, duplicates: rep == 2));
                        var b = SubsetModel.Sparse(OffsetTestUtil.RandomSorted(random, m, 600, duplicates: rep == 2));
                        var actual = a.Realize(random);
                        actual.Intersect(b.RealizeRef(random));
                        a.Intersect(b).AssertMatches(actual, $"{n} x {m}");
                        actual.Dispose();
                    }
                }
            }
        }

        static Func<int, bool> CutAt(int a, int b)
        {
            var lo = Math.Min(a, b);
            var hi = Math.Max(a, b);
            return r => r >= lo && r < hi;
        }

        static Func<int, bool> Modulus(int modulus, int residue) => r => r % modulus == residue % modulus;

    }

}

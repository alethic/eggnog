using System;
using System.Collections.Generic;
using System.Linq;

using Xunit;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// Tests for <see cref="Mask"/>, <see cref="MaskEnumerator"/> and <see cref="ArgGather"/>: the mask tests of egglog's
    /// core-relations/src/action/tests.rs, plus the set operations and the word boundary.
    /// </summary>
    public class MaskTests
    {

        static List<int> Lanes(Mask mask)
        {
            var lanes = new List<int>();
            foreach (var lane in mask)
                lanes.Add(lane);

            return lanes;
        }

        /// <summary>
        /// egglog's <c>mask_iter</c>.
        /// </summary>
        [Fact]
        public void MaskIter()
        {
            var offs = Enumerable.Range(0, 100).ToArray();
            var mask = new Mask(0, 100);
            var res = new List<int>();
            var visitor = new TestLaneVisitor(lane => res.Add(offs[lane]));
            mask.ForEach(ref visitor);
            Assert.Equal(offs, res);
        }

        /// <summary>
        /// egglog's <c>mask_iter_zip</c>.
        /// </summary>
        [Fact]
        public void MaskIterZip()
        {
            var offs1 = Enumerable.Range(0, 100).ToArray();
            var offs2 = Enumerable.Range(100, 100).ToArray();
            var mask = new Mask(0, 100);
            var res = new List<(int, int)>();
            var visitor = new TestLaneVisitor(lane => res.Add((offs1[lane], offs2[lane])));
            mask.ForEach(ref visitor);
            Assert.Equal(offs1.Zip(offs2).Select(p => (p.First, p.Second)), res);
        }

        /// <summary>
        /// egglog's <c>mask_iter_dyn</c>: sources <c>[Const 1, Slice [1, 3, 5], Const 1, Slice [2, 4, 6]]</c> read at
        /// lane 2 give <c>[1, 5, 1, 6]</c>. The slices are columns of a batch and the dynamic iterator is
        /// <see cref="ArgGather"/>.
        /// </summary>
        [Fact]
        public void MaskIterDyn()
        {
            var bindings = new Bindings();
            bindings.Insert(new Variable(0), [1, 3, 5]);
            bindings.Insert(new Variable(1), [2, 4, 6]);
            var mask = new Mask(0, 3);
            Assert.True(mask.Contains(2));

            QueryEntry[] sources = [new Value(1), new Variable(0), new Value(1), new Variable(1)];
            var gather = new ArgGather(sources, bindings, new uint[4], new int[8]);
            gather.Load(2);
            Assert.Equal(new uint[] { 1, 5, 1, 6 }, gather.Values.ToArray());
            gather.Load(0);
            Assert.Equal(new uint[] { 1, 1, 1, 2 }, gather.Values.ToArray());
            Assert.Throws<ArgumentOutOfRangeException>(() => gather.Load(Bindings.DefaultBatchSize));
            Assert.Throws<ArgumentException>(() => new ArgGather(sources, bindings, new uint[3], new int[8]));
            Assert.Throws<InvalidOperationException>(() => new ArgGather([new Variable(7)], bindings, new uint[4], new int[8]));
        }

        /// <summary>
        /// egglog's <c>retain</c>.
        /// </summary>
        [Fact]
        public void Retain()
        {
            var offs = Enumerable.Range(0, 100).ToArray();
            var mask = new Mask(0, 100);
            var filter = new TestLaneFilter(lane => offs[lane] % 2 == 0);
            mask.Retain(ref filter);
            var got = new List<int>();
            var visitor = new TestLaneVisitor(lane => got.Add(offs[lane]));
            mask.ForEach(ref visitor);
            Assert.Equal(offs.Where(x => x % 2 == 0), got);
            Assert.Equal(50, mask.Count);
            Assert.Equal(100, mask.Length);
        }

        /// <summary>
        /// egglog's <c>fill_vec</c>: failed lanes are dropped and get the default, as do dead ones.
        /// </summary>
        [Fact]
        public void FillVec()
        {
            var offs = Enumerable.Range(0, 100).Select(x => (uint)x).ToArray();
            var mask = new Mask(0, 100);
            var output = new uint[100];
            var producer = new TestLaneProducer(lane =>
            {
                Assert.Equal((uint)lane, offs[lane]);
                return offs[lane] % 2 == 0 ? offs[lane] : null;
            });
            mask.Fill(ref producer, output, 0, int.MaxValue);

            // We should filter the mask for the entries for which we returned 'None'.
            Assert.Equal(offs.Where(x => x % 2 == 0).Select(x => (int)x), Lanes(mask));

            // The output should have int.MaxValue at the odd indexes.
            for (var i = 0; i < output.Length; i++)
                Assert.Equal(i % 2 == 0 ? (uint)i : int.MaxValue, output[i]);

            // A second fill writes the default into the lanes that are now dead, and keeps the others.
            var again = new TestLaneProducer(lane => (uint)lane + 1000);
            mask.Fill(ref again, output, 0, 7);
            for (var i = 0; i < output.Length; i++)
                Assert.Equal(i % 2 == 0 ? (uint)i + 1000 : 7u, output[i]);
        }

        [Fact]
        public void FillWritesAtTheOffsetAndChecksTheRange()
        {
            var mask = new Mask(1, 4);
            var output = new uint[10];
            var producer = new TestLaneProducer(lane => (uint)lane * 10);
            mask.Fill(ref producer, output, 5, 99);
            Assert.Equal(new uint[] { 0, 0, 0, 0, 0, 99, 10, 20, 30, 0 }, output);
            Assert.Throws<ArgumentOutOfRangeException>(() =>
            {
                var m = new Mask(4);
                var p = new TestLaneProducer(lane => 1);
                m.Fill(ref p, new uint[6], 3, 0);
            });
        }

        [Fact]
        public void AssignAndRetainLeavesFailedCellsAlone()
        {
            var mask = new Mask(0, 6);
            mask.Remove(4);
            var output = new uint[] { 1, 1, 1, 1, 1, 1 };
            var producer = new TestLaneProducer(lane => lane == 2 ? null : (uint)lane + 10);
            mask.AssignAndRetain(ref producer, output, 0);
            Assert.Equal(new uint[] { 10, 11, 1, 13, 1, 15 }, output);
            Assert.Equal(new[] { 0, 1, 3, 5 }, Lanes(mask));
        }

        [Fact]
        public void RangesSpanTheWordBoundary()
        {
            Assert.Equal(Enumerable.Range(60, 10), Lanes(new Mask(60, 70)));
            Assert.Equal(Enumerable.Range(0, 128), Lanes(new Mask(128)));
            Assert.Equal(Enumerable.Range(64, 64), Lanes(new Mask(64, 128)));
            Assert.Equal(Enumerable.Range(0, 64), Lanes(new Mask(64)));
            Assert.Empty(Lanes(new Mask(0)));
            Assert.True(new Mask(5, 5).IsEmpty);
            Assert.Equal(5, new Mask(5, 5).Length);

            var full = new Mask(128);
            Assert.Equal(128, full.Count);
            Assert.Equal(ulong.MaxValue, full.Low);
            Assert.Equal(ulong.MaxValue, full.High);
            Assert.True(full.Contains(127));
            Assert.False(full.Contains(128));
            Assert.False(full.Contains(-1));

            Assert.Throws<ArgumentOutOfRangeException>(() => new Mask(129));
            Assert.Throws<ArgumentOutOfRangeException>(() => new Mask(5, 4));
            Assert.Throws<ArgumentOutOfRangeException>(() => new Mask(-1, 4));
        }

        [Fact]
        public void LanesAreVisitedInAscendingOrderAcrossWords()
        {
            var mask = new Mask(128);
            var filter = new TestLaneFilter(lane => lane % 3 == 0);
            mask.Retain(ref filter);
            var seen = new List<int>();
            var visitor = new TestLaneVisitor(seen.Add);
            mask.ForEach(ref visitor);
            Assert.Equal(Enumerable.Range(0, 128).Where(x => x % 3 == 0), seen);
            Assert.Equal(seen, Lanes(mask));

            var small = new Mask(8);
            var thirds = new TestLaneFilter(lane => lane % 3 == 0);
            small.Retain(ref thirds);
            Assert.Equal("{0, 3, 6}/8", small.ToString());
        }

        [Fact]
        public void RemoveClearAndContains()
        {
            var mask = new Mask(100);
            mask.Remove(3);
            mask.Remove(70);
            Assert.False(mask.Contains(3));
            Assert.False(mask.Contains(70));
            Assert.True(mask.Contains(71));
            Assert.Equal(98, mask.Count);
            Assert.Throws<ArgumentOutOfRangeException>(() =>
            {
                var m = new Mask(10);
                m.Remove(10);
            });

            mask.Clear();
            Assert.True(mask.IsEmpty);
            Assert.Equal(0, mask.Count);
            Assert.Equal(100, mask.Length);
        }

        [Fact]
        public void SymmetricDifferenceAndUnionComputeTheFallbackLanes()
        {
            var live = new Mask(100);
            live.Remove(10);
            var found = live;
            var filter = new TestLaneFilter(lane => lane % 2 == 0);
            found.Retain(ref filter);
            Assert.True(found.IsSubsetOf(live));
            Assert.False(live.IsSubsetOf(found));

            var missed = found;
            missed.SymmetricDifference(live);
            Assert.Equal(Enumerable.Range(0, 100).Where(x => x % 2 == 1), Lanes(missed));

            var fallbackOk = missed;
            var half = new TestLaneFilter(lane => lane < 50);
            fallbackOk.Retain(ref half);
            found.Union(fallbackOk);
            Assert.Equal(Enumerable.Range(0, 100).Where(x => x != 10 && (x % 2 == 0 || x < 50)), Lanes(found));
        }

        [Fact]
        public void EnumeratorIteratesASnapshot()
        {
            var mask = new Mask(10);
            var lanes = new List<int>();
            foreach (var lane in mask)
            {
                lanes.Add(lane);
                if (lane == 0)
                    mask.Clear();
            }

            Assert.Equal(Enumerable.Range(0, 10), lanes);
            Assert.True(mask.IsEmpty);
        }

    }

}

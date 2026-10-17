using System;
using System.Collections.Generic;

using Xunit;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// Tests for <see cref="PredictedValues"/>: egglog's <c>PredictedVals</c> (core-relations/src/action/mod.rs:292-323).
    /// </summary>
    public class PredictedValuesTests
    {

        static int Add(PredictedValues p, TableId table, uint[] row, int keyLength)
        {
            var offset = p.FindOrBegin(table, row, 0, keyLength, row.Length, out var found);
            Assert.False(found);
            Array.Copy(row, keyLength, p.Cells, offset + keyLength, row.Length - keyLength);
            p.Commit();
            return offset;
        }

        [Fact]
        public void FindsRowsByTableAndKey()
        {
            var p = new PredictedValues();
            var t0 = new TableId(0);
            var t1 = new TableId(1);
            Add(p, t0, [1, 2, 100], 2);
            Add(p, t1, [1, 2, 200], 2);
            Add(p, t0, [2, 1, 300], 2);
            Assert.Equal(3, p.Count);

            Assert.True(p.TryGetRow(t0, [1, 2], 0, 2, out var a));
            Assert.Equal(100u, p.Cells[a + 2]);
            Assert.True(p.TryGetRow(t1, [9, 1, 2], 1, 2, out var b));
            Assert.Equal(200u, p.Cells[b + 2]);
            Assert.True(p.TryGetRow(t0, [2, 1], 0, 2, out var c));
            Assert.Equal(300u, p.Cells[c + 2]);
            Assert.False(p.TryGetRow(t1, [2, 1], 0, 2, out _));
            Assert.False(p.TryGetRow(t0, [1], 0, 1, out _));

            var again = p.FindOrBegin(t0, [1, 2], 0, 2, 3, out var found);
            Assert.True(found);
            Assert.Equal(a, again);
        }

        [Fact]
        public void AReservationCopiesTheKeyFromItsOffset()
        {
            var p = new PredictedValues();
            var t = new TableId(0);
            var offset = p.FindOrBegin(t, [9, 5, 6], 1, 2, 3, out var found);
            Assert.False(found);
            Assert.Equal(new uint[] { 5, 6 }, p.Cells.AsSpan(offset, 2).ToArray());
            p.Commit();

            Assert.Equal(offset, p.FindOrBegin(t, [5, 6], 0, 2, 3, out found));
            Assert.True(found);
        }

        [Fact]
        public void AnAbandonedReservationLeavesTheCacheUnchanged()
        {
            var p = new PredictedValues();
            var t = new TableId(0);
            p.FindOrBegin(t, [5], 0, 1, 2, out var found);
            Assert.False(found);
            Assert.Equal(0, p.Count);

            // The next lookup forgets the reservation; the key is still absent.
            Assert.False(p.TryGetRow(t, [5], 0, 1, out _));
            var offset = Add(p, t, [5, 50], 1);
            Assert.Equal(0, offset);
            Assert.True(p.TryGetRow(t, [5], 0, 1, out var row));
            Assert.Equal(50u, p.Cells[row + 1]);
            Assert.Throws<InvalidOperationException>(() => p.Commit());
        }

        [Fact]
        public void GrowsAndClears()
        {
            var p = new PredictedValues();
            var t = new TableId(3);
            var offsets = new Dictionary<uint, int>();
            for (uint k = 0; k < 5000; k++)
                offsets[k] = Add(p, t, [k, k * 2, k * 3], 1);

            Assert.Equal(5000, p.Count);
            for (uint k = 0; k < 5000; k++)
            {
                Assert.True(p.TryGetRow(t, [k], 0, 1, out var row));
                Assert.Equal(offsets[k], row);
                Assert.Equal(k * 2, p.Cells[row + 1]);
                Assert.Equal(k * 3, p.Cells[row + 2]);
            }

            p.Clear();
            Assert.Equal(0, p.Count);
            Assert.False(p.TryGetRow(t, [7], 0, 1, out _));
            Assert.Equal(0, Add(p, t, [7, 1, 1], 1));
        }

        [Fact]
        public void EmptyKeysAndBadArguments()
        {
            var p = new PredictedValues();
            var t = new TableId(0);
            Add(p, t, [42], 0);
            Assert.True(p.TryGetRow(t, [], 0, 0, out var row));
            Assert.Equal(42u, p.Cells[row]);
            Assert.False(p.TryGetRow(new TableId(1), [], 0, 0, out _));

            Assert.Throws<ArgumentOutOfRangeException>(() => p.FindOrBegin(t, [1, 2], 0, 2, 1, out _));
            Assert.Throws<ArgumentOutOfRangeException>(() => p.TryGetRow(t, [1], 1, 1, out _));
            Assert.Throws<ArgumentNullException>(() => p.TryGetRow(t, null!, 0, 0, out _));
            Assert.True(p.GetProbeBuffer(20).Length >= 20);
            Assert.Same(p.GetProbeBuffer(3), p.GetProbeBuffer(5));
        }

        [Fact]
        public void TheHashMixesTheTable()
        {
            uint[] key = [1, 2, 3];
            Assert.NotEqual(PredictedValues.Hash(new TableId(0), key, 0, 3), PredictedValues.Hash(new TableId(1), key, 0, 3));
            Assert.Equal(PredictedValues.Hash(new TableId(2), key, 1, 2), PredictedValues.Hash(new TableId(2), [2, 3], 0, 2));
        }

    }

}

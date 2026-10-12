using System;
using System.Linq;

using Xunit;

using static Eggnog.Relations.Tests.ActionTestUtil;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// Tests for <see cref="VectorizedLookup"/>: egglog's <c>lookup_row_vectorized</c> and
    /// <c>lookup_with_default_vectorized</c> (core-relations/src/table_spec.rs:490-544), for both table classes and every
    /// key width.
    /// </summary>
    public class VectorizedLookupTests
    {

        /// <summary>
        /// Builds a table with <paramref name="keyCount"/> key columns and one value column whose rows are the keys
        /// <c>(i, i + 1, ...)</c> for even <c>i</c> below 20, with value <c>1000 + i</c>.
        /// </summary>
        static SortedWritesTable Table(int keyCount)
        {
            var table = Plain(keyCount, keyCount + 1);
            var buffer = table.NewBuffer();
            for (uint i = 0; i < 20; i += 2)
            {
                var row = new uint[keyCount + 1];
                for (var c = 0; c < keyCount; c++)
                    row[c] = i + (uint)c;

                row[keyCount] = 1000 + i;
                buffer.StageInsert(row);
            }

            buffer.Flush();
            table.Merge(TableTestUtil.EmptyState());
            return table;
        }

        [Theory]
        [InlineData(1)]
        [InlineData(2)]
        [InlineData(3)]
        [InlineData(4)]
        [InlineData(5)]
        public void LookupRowAtEveryKeyWidth(int keyCount)
        {
            var table = Table(keyCount);
            var b = new Bindings();
            for (var c = 0; c < keyCount; c++)
                b.Insert(V(c), Enumerable.Range(0, 20).Select(i => (uint)(i + c)).ToArray());

            var mask = new Mask(20);
            mask.Remove(4);
            var args = Enumerable.Range(0, keyCount).Select(c => QueryEntry.Var(V(c))).ToArray();
            VectorizedLookup.LookupRow(table, ref mask, b, args, C(keyCount), V(10));
            var expected = Enumerable.Range(0, 20).Select(i => i % 2 == 0 && i != 4 ? 1000u + (uint)i : S).ToArray();
            Assert.Equal(expected, Column(b, 10));
            Assert.Equal(Enumerable.Range(0, 20).Where(i => i % 2 == 0 && i != 4), Lanes(mask));

            // The same lookup with a default keeps every live lane.
            var all = new Mask(20);
            all.Remove(4);
            VectorizedLookup.LookupWithDefault(table, ref all, b, args, C(keyCount), K(7), V(11));
            Assert.Equal(Enumerable.Range(0, 20).Select(i => i == 4 ? S : i % 2 == 0 ? 1000u + (uint)i : 7u), Column(b, 11));
            Assert.Equal(19, all.Count);

            // Reading a key column back gives the key.
            var keys = new Mask(20);
            VectorizedLookup.LookupRow(table, ref keys, b, args, C(0), V(12));
            Assert.Equal(Enumerable.Range(0, 20).Select(i => i % 2 == 0 ? (uint)i : S), Column(b, 12));
        }

        [Fact]
        public void KeysMayMixConstantsAndVariables()
        {
            var table = Table(4);
            var b = Batch((0, [2, 3, 4]), (1, [5, 6, 7]));
            var mask = new Mask(3);
            VectorizedLookup.LookupRow(table, ref mask, b, [K(4), V(1), K(6), K(7)], C(4), V(2));
            Assert.Equal(new uint[] { 1004, S, S }, Column(b, 2));
            Assert.Equal(new[] { 0 }, Lanes(mask));

            var two = new Mask(3);
            VectorizedLookup.LookupWithDefault(Table(2), ref two, b, [V(0), K(3)], C(2), V(1), V(3));
            Assert.Equal(new uint[] { 1002, 6, 7 }, Column(b, 3));
        }

        [Fact]
        public void TablesWithoutKeyColumns()
        {
            var table = Plain(0, 1);
            TableTestUtil.Stage(table, [77]);
            table.Merge(TableTestUtil.EmptyState());
            var b = Batch((0, [1, 2]));
            var mask = new Mask(2);
            VectorizedLookup.LookupRow(table, ref mask, b, [], C(0), V(1));
            Assert.Equal(new uint[] { 77, 77 }, Column(b, 1));

            var empty = Plain(0, 1);
            var none = new Mask(2);
            VectorizedLookup.LookupRow(empty, ref none, b, [], C(0), V(2));
            Assert.True(none.IsEmpty);
        }

        [Fact]
        public void TheUnionFindAnswersCanonicalIdsForEveryId()
        {
            var uf = new DisplacedTable();
            var buffer = uf.NewBuffer();
            buffer.StageInsert([3, 1, 0]);
            buffer.StageInsert([5, 3, 1]);
            buffer.Flush();
            uf.Merge(TableTestUtil.EmptyState());

            var b = Batch((0, [1, 3, 5, 9, 1000]));
            var mask = new Mask(5);
            VectorizedLookup.LookupRow(uf, ref mask, b, [V(0)], C(1), V(1));
            Assert.Equal(new uint[] { 1, 1, 1, 9, 1000 }, Column(b, 1));
            Assert.Equal(5, mask.Count);

            // Columns 0 and 2 exist only for displaced ids.
            var displaced = new Mask(5);
            VectorizedLookup.LookupRow(uf, ref displaced, b, [V(0)], C(2), V(2));
            Assert.Equal(new[] { 1, 2 }, Lanes(displaced));
            Assert.Equal(new uint[] { S, 0, 1, S, S }, Column(b, 2));

            var defaulted = new Mask(5);
            VectorizedLookup.LookupWithDefault(uf, ref defaulted, b, [V(0)], C(0), V(0), V(3));
            Assert.Equal(new uint[] { 1, 3, 5, 9, 1000 }, Column(b, 3));
            Assert.Throws<ArgumentException>(() =>
            {
                var m = new Mask(5);
                VectorizedLookup.LookupRow(uf, ref m, b, [V(0), V(0)], C(1), V(4));
            });
        }

        [Fact]
        public void LookupsSkipDeletedRows()
        {
            var table = Table(1);
            TableTestUtil.StageRemove(table, [2]);
            table.Merge(TableTestUtil.EmptyState());
            var b = Batch((0, [0, 2, 4]));
            var mask = new Mask(3);
            VectorizedLookup.LookupRow(table, ref mask, b, [V(0)], C(1), V(1));
            Assert.Equal(new uint[] { 1000, S, 1004 }, Column(b, 1));
        }

        [Fact]
        public void BadArgumentsThrowBeforeAnythingIsWritten()
        {
            var table = Table(1);
            var b = Batch((0, [0]));
            var mask = new Mask(1);
            Assert.Throws<ArgumentOutOfRangeException>(() => VectorizedLookup.LookupRow(table, ref mask, b, [V(0)], C(2), V(1)));
            Assert.Throws<ArgumentException>(() => VectorizedLookup.LookupRow(table, ref mask, b, [V(0), V(0)], C(1), V(1)));
            Assert.Throws<ArgumentNullException>(() => VectorizedLookup.LookupRow((Table)null!, ref mask, b, [V(0)], C(1), V(1)));
            Assert.False(b.Contains(V(1)));
            Assert.Throws<ArgumentException>(() =>
            {
                var m = new Mask(10);
                VectorizedLookup.LookupRow(table, ref m, new Bindings(4), [K(0)], C(1), V(1));
            });
        }

        static int[] Lanes(Mask mask)
        {
            var lanes = new System.Collections.Generic.List<int>();
            foreach (var lane in mask)
                lanes.Add(lane);

            return lanes.ToArray();
        }

    }

}

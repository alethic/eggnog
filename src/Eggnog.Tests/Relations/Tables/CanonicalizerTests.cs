using System;
using System.Collections.Generic;
using System.Linq;

using Xunit;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// Tests for <see cref="Canonicalizer"/>, the union-find's <see cref="Rebuilder"/>: the column-count specializations
    /// of <c>rebuild_buf</c> against a naive reference, <c>rebuild_subset</c>, <c>rebuild_slice</c> and
    /// <c>rebuild_val</c>.
    /// </summary>
    public class CanonicalizerTests
    {

        static uint[] R(params uint[] cells) => cells;

        static ColumnId C(int column) => new(column);

        static DisplacedTable UnionFind(params (uint A, uint B)[] unions)
        {
            var state = TableTestUtil.EmptyState();
            var d = new DisplacedTable();
            TableTestUtil.Stage(d, unions.Select(u => R(u.A, u.B, 1)).ToArray());
            d.Merge(state);
            return d;
        }

        [Fact]
        public void HintColumnIsTheDisplacedId()
        {
            var d = UnionFind((1, 2));
            var rebuilder = d.GetRebuilder([C(0)]);
            Assert.NotNull(rebuilder);
            Assert.Equal(C(0), rebuilder.HintColumn);
            Assert.Equal(1u, rebuilder.RebuildValue(2));
            Assert.Equal(7u, rebuilder.RebuildValue(7));
            Assert.Equal(Value.StaleRep, rebuilder.RebuildValue(Value.StaleRep));
        }

        [Theory]
        [InlineData(1)]
        [InlineData(2)]
        [InlineData(3)]
        [InlineData(4)]
        [InlineData(5)]
        public void RebuildBufferEmitsExactlyTheChangedLiveRows(int columnCount)
        {
            var random = new Random(columnCount * 31);
            var unions = Enumerable.Range(0, 40).Select(_ => ((uint)random.Next(60), (uint)random.Next(60))).ToArray();
            var d = UnionFind(unions);
            var columns = Enumerable.Range(0, columnCount).Select(i => C(i + 1)).ToArray();
            var rebuilder = d.GetRebuilder(columns)!;

            const int Arity = 7;
            var buffer = new RowBuffer(Arity);
            for (var r = 0; r < 300; r++)
            {
                var row = new uint[Arity];
                for (var c = 0; c < Arity; c++)
                    row[c] = (uint)random.Next(80);

                buffer.AddRow(row);
                if (r % 17 == 0)
                    buffer.SetStale(r);
            }

            var output = new TaggedRowBuffer(Arity);
            rebuilder.RebuildBuffer(buffer, 10, 250, output, TableTestUtil.EmptyState());

            var expected = new List<(int, uint[])>();
            for (var r = 10; r < 250; r++)
            {
                if (buffer.IsStale(r))
                    continue;

                var row = buffer.GetRow(r).ToArray();
                var changed = false;
                foreach (var column in columns)
                {
                    var canonical = d.FindNaive(row[column.Rep]);
                    changed |= canonical != row[column.Rep];
                    row[column.Rep] = canonical;
                }

                if (changed)
                    expected.Add((r, row));
            }

            var actual = TableTestUtil.DumpBuffer(output);
            Assert.Equal(expected.Count, actual.Count);
            for (var i = 0; i < expected.Count; i++)
            {
                Assert.Equal(expected[i].Item1, actual[i].Id);
                Assert.Equal(expected[i].Item2, actual[i].Row);
            }

            Assert.True(expected.Count > 0);
        }

        [Fact]
        public void RebuildBufferChecksItsRange()
        {
            var d = UnionFind((1, 2));
            var rebuilder = d.GetRebuilder([C(0)])!;
            var buffer = new RowBuffer(2);
            buffer.AddRow(R(2, 2));
            var output = new TaggedRowBuffer(2);

            rebuilder.RebuildBuffer(buffer, 1, 1, output, TableTestUtil.EmptyState());
            Assert.True(output.IsEmpty);
            Assert.Throws<ArgumentOutOfRangeException>(() => rebuilder.RebuildBuffer(buffer, 0, 2, output, TableTestUtil.EmptyState()));

            var wide = d.GetRebuilder([C(5)])!;
            Assert.Throws<ArgumentOutOfRangeException>(() => wide.RebuildBuffer(buffer, 0, 1, output, TableTestUtil.EmptyState()));
        }

        [Fact]
        public void RebuildSubsetScansLiveRowsAndTombstonesUnchangedCopies()
        {
            var state = TableTestUtil.EmptyState();
            var d = UnionFind((1, 2), (3, 4));
            var table = new SortedWritesTable(1, 3, null, [C(0), C(1)], ClosureMerge.TakeNew, ParallelHeuristics.Serial);
            TableTestUtil.Stage(table, R(2, 5, 9), R(5, 6, 4), R(6, 4, 4), R(7, 7, 4));
            table.Merge(state);
            TableTestUtil.StageRemove(table, R(7));
            table.Merge(state);

            var rebuilder = d.GetRebuilder(table.RebuildColumns)!;
            var output = new TaggedRowBuffer(3);
            output.AddRow(99, R(0, 0, 0));
            rebuilder.RebuildSubset(table, table.All().AsRef(), output, state);

            var rows = TableTestUtil.DumpBuffer(output);
            Assert.Equal(4, rows.Count);
            Assert.Equal((99, R(0, 0, 0)), (rows[0].Id, rows[0].Row), new RowTupleEquality());
            Assert.Equal((0, R(1, 5, 9)), (rows[1].Id, rows[1].Row), new RowTupleEquality());
            Assert.True(output.IsStale(2));
            Assert.Equal((2, R(6, 3, 4)), (rows[3].Id, rows[3].Row), new RowTupleEquality());

            // Column 2 is not rebuilt, even though 4 is not canonical.
            Assert.Equal(4u, rows[3].Row[2]);
        }

        [Fact]
        public void RebuildSliceCanonicalizesEveryValue()
        {
            var d = UnionFind((1, 2), (2, 3));
            var rebuilder = d.GetRebuilder([C(0)])!;
            var values = R(3, 2, 1, 9);
            Assert.True(rebuilder.RebuildSlice(values));
            Assert.Equal(R(1, 1, 1, 9), values);
            Assert.False(rebuilder.RebuildSlice(values));
        }

    }

}

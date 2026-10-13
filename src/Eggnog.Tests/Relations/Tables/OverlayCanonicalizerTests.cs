using System;
using System.Collections.Generic;
using System.Linq;

using Xunit;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// Tests for <see cref="OverlayCanonicalizer"/>, a child context's rebuilder (colored-contexts spec §5.5): the
    /// shared loops of <see cref="CanonicalizeLoops"/> give exactly the root canonicalizer's output when the overlay is
    /// empty and the child's find otherwise, and a table rebuilds against a child's union-find table on both the full
    /// and the incremental (hint column) paths.
    /// </summary>
    public class OverlayCanonicalizerTests
    {

        static ColumnId C(int column) => new(column);

        /// <summary>
        /// A root union-find with random unions over [0, 60), and a child's table whose overlay holds
        /// <paramref name="childUnions"/> random unions.
        /// </summary>
        static (DisplacedTable Root, OverlayDisplacedTable Child) Setup(int seed, int childUnions)
        {
            var random = new Random(seed);
            var root = new DisplacedTable();
            for (var i = 0; i < 25; i++)
                TableTestUtil.Stage(root, [(uint)random.Next(60), (uint)random.Next(60), 1]);

            root.Merge(TableTestUtil.EmptyState());
            var child = new OverlayDisplacedTable(new LayeredUnionFind(root.UnionFind), new UnionFindOverlay(), new DisplacedJournal(), root);
            for (var i = 0; i < childUnions; i++)
                TableTestUtil.Stage(child, [(uint)random.Next(80), (uint)random.Next(80), 2]);

            child.Merge(TableTestUtil.EmptyState());
            return (root, child);
        }

        static RowBuffer RandomRows(int seed, int arity)
        {
            var random = new Random(seed);
            var buffer = new RowBuffer(arity);
            for (var r = 0; r < 300; r++)
            {
                var row = new uint[arity];
                for (var c = 0; c < arity; c++)
                    row[c] = (uint)random.Next(80);

                buffer.AddRow(row);
                if (r % 17 == 0)
                    buffer.SetStale(r);
            }

            return buffer;
        }

        [Theory]
        [InlineData(1)]
        [InlineData(2)]
        [InlineData(3)]
        [InlineData(4)]
        [InlineData(5)]
        public void AnEmptyOverlayRebuildsExactlyAsTheRoot(int columnCount)
        {
            var (root, child) = Setup(columnCount, 0);
            Assert.True(child.Overlay.IsEmpty);
            var columns = Enumerable.Range(0, columnCount).Select(i => C(i + 1)).ToArray();
            var rootRebuilder = root.GetRebuilder(columns)!;
            var childRebuilder = child.GetRebuilder(columns)!;
            var buffer = RandomRows(columnCount * 7, 7);

            var expected = new TaggedRowBuffer(7);
            var actual = new TaggedRowBuffer(7);
            rootRebuilder.RebuildBuffer(buffer, 10, 250, expected, TableTestUtil.EmptyState());
            childRebuilder.RebuildBuffer(buffer, 10, 250, actual, TableTestUtil.EmptyState());
            var want = TableTestUtil.DumpBuffer(expected);
            var got = TableTestUtil.DumpBuffer(actual);
            Assert.Equal(want.Count, got.Count);
            for (var i = 0; i < want.Count; i++)
                Assert.Equal(want[i], got[i], new RowTupleEquality());

            Assert.True(expected.Count > 0);

            for (uint v = 0; v < 90; v++)
                Assert.Equal(rootRebuilder.RebuildValue(v), childRebuilder.RebuildValue(v));
        }

        [Theory]
        [InlineData(1)]
        [InlineData(2)]
        [InlineData(3)]
        [InlineData(4)]
        [InlineData(5)]
        public void RebuildBufferAppliesTheChildsFind(int columnCount)
        {
            var (_, child) = Setup(columnCount + 100, 30);
            Assert.False(child.Overlay.IsEmpty);
            var columns = Enumerable.Range(0, columnCount).Select(i => C(i + 1)).ToArray();
            var rebuilder = child.GetRebuilder(columns)!;
            Assert.Equal(C(0), rebuilder.HintColumn);
            var buffer = RandomRows(columnCount * 13, 7);
            var output = new TaggedRowBuffer(7);
            rebuilder.RebuildBuffer(buffer, 0, 300, output, TableTestUtil.EmptyState());

            var expected = new List<(int, uint[])>();
            for (var r = 0; r < 300; r++)
            {
                if (buffer.IsStale(r))
                    continue;

                var row = buffer.GetRow(r).ToArray();
                var changed = false;
                foreach (var column in columns)
                {
                    var canonical = child.FindNaive(row[column.Rep]);
                    changed |= canonical != row[column.Rep];
                    row[column.Rep] = canonical;
                }

                if (changed)
                    expected.Add((r, row));
            }

            var actual = TableTestUtil.DumpBuffer(output);
            Assert.Equal(expected.Count, actual.Count);
            for (var i = 0; i < expected.Count; i++)
                Assert.Equal(expected[i], (actual[i].Id, actual[i].Row), new RowTupleEquality());

            Assert.True(expected.Count > 0);
            Assert.Throws<ArgumentOutOfRangeException>(() => rebuilder.RebuildBuffer(buffer, 0, 301, output, TableTestUtil.EmptyState()));
        }

        [Fact]
        public void RebuildSubsetAndSliceUseTheChildsFind()
        {
            var root = new DisplacedTable();
            TableTestUtil.Stage(root, [2, 1, 1]);
            root.Merge(TableTestUtil.EmptyState());
            var child = new OverlayDisplacedTable(new LayeredUnionFind(root.UnionFind), new UnionFindOverlay(), new DisplacedJournal(), root);
            TableTestUtil.Stage(child, [4, 3, 2], [3, 2, 2]);
            child.Merge(TableTestUtil.EmptyState());
            Assert.Equal(1u, child.FindNaive(4));

            var state = TableTestUtil.EmptyState();
            var table = new SortedWritesTable(1, 3, null, [C(0), C(1)], ClosureMerge.TakeNew, ParallelHeuristics.Serial);
            TableTestUtil.Stage(table, [4, 5, 9], [5, 6, 4], [6, 2, 4]);
            table.Merge(state);

            var rebuilder = (OverlayCanonicalizer)child.GetRebuilder(table.RebuildColumns)!;
            var output = new TaggedRowBuffer(3);
            rebuilder.RebuildSubset(table, table.All().AsRef(), output, state);
            var rows = TableTestUtil.DumpBuffer(output);
            Assert.Equal(3, rows.Count);
            Assert.Equal((0, new uint[] { 1, 5, 9 }), (rows[0].Id, rows[0].Row), new RowTupleEquality());
            Assert.True(output.IsStale(1));
            Assert.Equal((2, new uint[] { 6, 1, 4 }), (rows[2].Id, rows[2].Row), new RowTupleEquality());

            var values = new uint[] { 4, 3, 2, 1, 9 };
            Assert.True(rebuilder.RebuildSlice(values));
            Assert.Equal(new uint[] { 1, 1, 1, 1, 9 }, values);
            Assert.False(rebuilder.RebuildSlice(values));

            var single = new uint[] { 7, 4, 0 };
            Assert.True(rebuilder.RebuildRow(single));
            Assert.Equal(new uint[] { 7, 1, 0 }, single);
        }

        /// <summary>
        /// A catalog with a child's union-find at id 0 (over a root union-find) and <c>f(a) = r</c> at id 1, columns
        /// <c>[a, r, ts]</c>, both ids rebuildable, a union-id merge into the child's union-find.
        /// </summary>
        static (TableCatalog Catalog, OverlayDisplacedTable Uf, SortedWritesTable F) Catalog()
        {
            var root = new DisplacedTable();
            var catalog = new TableCatalog();
            var uf = new OverlayDisplacedTable(new LayeredUnionFind(root.UnionFind), new UnionFindOverlay(), new DisplacedJournal(), root);
            var ufId = catalog.Add(uf);
            var f = new SortedWritesTable(1, 3, C(2), [C(0), C(1)], new UnionIdMerge(C(1), ufId, C(2)), ParallelHeuristics.Serial);
            catalog.Add(f);
            return (catalog, uf, f);
        }

        [Theory]
        [InlineData(100)]
        [InlineData(20_000)]
        public void ATableRebuildsAgainstAChildsUnionFind(int rows)
        {
            // Below 10 000 rows the rebuild takes the full path; above, with a small journal, the incremental path through
            // the hint column (the journal) and the rebuild index. Both must leave the same canonical rows.
            var (catalog, uf, f) = Catalog();
            var ufId = new TableId(0);
            var state = new ExecutionState(catalog, 1);
            var staged = new List<uint[]>();
            for (uint i = 0; i < rows; i++)
                staged.Add([i, 100_000 + i, 1]);

            TableTestUtil.Stage(f, staged.ToArray());
            f.Merge(state);

            // The child merges 3 into 1 and 5 into 4; 100 003 and 100 005 are outputs and also merge.
            TableTestUtil.Stage(uf, [3, 1, 2], [5, 4, 2], [100_003, 100_001, 2]);
            uf.Merge(state);
            Assert.Equal(3, uf.Count);

            Assert.True(f.ApplyRebuild(ufId, uf, 3, state));
            Assert.Equal(rows > 10_000, f.RebuildIndex is not null);
            state.Flush();
            var merge = new ExecutionState(catalog, 3);
            f.Merge(merge);
            merge.Flush();
            uf.Merge(merge);

            // f(3) = 100 003 became f(1) = 100 001 (after the union), colliding with f(1) = 100 001; f(5) = 100 005 became
            // f(4), whose union-id merge staged 100 004 = 100 005.
            var live = TableTestUtil.LiveRows(f).ToDictionary(r => r[0]);
            Assert.Equal(rows - 2, live.Count);
            Assert.False(live.ContainsKey(3));
            Assert.False(live.ContainsKey(5));
            Assert.Equal(100_001u, live[1][1]);
            Assert.Equal(uf.FindNaive(100_005), uf.FindNaive(100_004));
            Assert.All(live.Values, r => Assert.Equal(r[0], uf.FindNaive(r[0])));
        }

    }

}

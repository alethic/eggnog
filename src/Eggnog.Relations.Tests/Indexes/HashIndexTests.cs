using System;
using System.Linq;

using Xunit;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// Port of egglog's core-relations/src/hash_index/tests.rs.
    /// </summary>
    [Collection(IndexStressCollection.Name)]
    public class HashIndexTests
    {

        static uint[] R(params uint[] cells) => cells;

        static ColumnId C(int column) => new(column);

        /// <summary>
        /// Checks that every key <c>[i, i + 2]</c> for <c>i</c> in <c>[0, count)</c> finds exactly the row
        /// <c>[i, i + 1, i + 2, _]</c>, and that the row reads back by its key with the same id.
        /// </summary>
        static void CheckKeys(SortedWritesTable table, Index<TupleIndex> index, int count)
        {
            for (uint i = 0; i < count; i++)
            {
                Assert.True(index.Data.TryGetSubset(i, i + 2, out var subset), $"key [{i}, {i + 2}] is missing");
                var rows = TableTestUtil.DumpSubset(table, subset);
                Assert.NotEmpty(rows);
                foreach (var (id, row) in rows)
                {
                    Assert.Equal(R(i, i + 1, i + 2), row[..3]);
                    var readback = table.GetRow(row.AsSpan(0, 2));
                    Assert.NotNull(readback);
                    Assert.Equal(id, readback.Value.Id);
                    Assert.Equal(row, readback.Value.Values);
                }
            }
        }

        /// <summary>
        /// Port of <c>basic_updates</c>: an index over columns (0, 2) of a sorted-writes table answers after its first
        /// refresh, after an incremental refresh over new rows, and after a full refresh once the table's major version
        /// changed. egglog repeats it ten times for coverage of its nondeterministic parallelism; this runs it serially
        /// and with four threads.
        /// </summary>
        [Theory]
        [InlineData(1)]
        [InlineData(4)]
        public void BasicUpdates(int threads)
        {
            for (var round = 0; round < 10; round++)
            {
                var parallelism = new ParallelHeuristics(threads);
                var table = TableTestUtil.FillTable(
                    [R(0, 1, 2, 0), R(1, 2, 3, 0), R(2, 3, 4, 0), R(3, 4, 5, 1), R(4, 5, 6, 1)],
                    2,
                    C(3),
                    new ClosureMerge((old, incoming) =>
                    {
                        Assert.Equal(old, incoming);
                        return null;
                    }),
                    parallelism);

                var index = new Index<TupleIndex>([C(0), C(2)], new TupleIndex(2, parallelism));
                Assert.False(index.Data.TryGetSubset(0, 2, out _));
                index.Refresh(table);
                CheckKeys(table, index, 5);

                var buffer = table.NewBuffer();
                for (uint i = 5; i < 10; i++)
                    buffer.StageInsert(R(i, i + 1, i + 2, 2));

                buffer.Flush();
                var state = TableTestUtil.EmptyState();
                table.Merge(state);
                index.Refresh(table);
                CheckKeys(table, index, 10);

                // Now get an update to the major version.
                var start = table.Version.Major;
                while (table.Version.Major == start)
                {
                    TableTestUtil.StageRemove(table, R(0, 1));
                    table.Merge(state);
                    TableTestUtil.Stage(table, R(0, 1, 2, 3));
                    table.Merge(state);
                }

                // Refresh should do the right thing.
                index.Refresh(table);
                Assert.Equal(table.Version, index.UpdatedTo);
                CheckKeys(table, index, 10);
            }
        }

    }

}

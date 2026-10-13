using System;
using System.Linq;

using Xunit;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// Review tests: per-subset indexes (egglog's <c>group_by_col</c> / <c>group_by_key</c>, which the join builds for
    /// every probed trie node and, for multi-column keys, on every <c>get_index</c> call, execute.rs:873-893) draw their
    /// hash tables from thread-local pools in egglog (<c>ColumnIndexShard.table: Pooled&lt;IndexMap&gt;</c>,
    /// <c>SubsetTable.hash: Pooled&lt;HashTable&gt;</c>, <c>keys: RowBuffer</c> over a pooled <c>Vec</c>;
    /// core-relations/src/hash_index/mod.rs:123-144, 191-203, 490-501; pool/mod.rs:424-446). The conventions require
    /// pooling where the Rust pools, and the Memory module already defines the matching limits
    /// (<see cref="PoolLimits.ColumnIndex"/>, <see cref="PoolLimits.TupleIndexes"/>), but nothing uses them: every
    /// build allocates fresh hash-table storage sized to the subset, and <c>Dispose</c> returns only the arena.
    /// </summary>
    [Collection(IndexStressCollection.Name)]
    public class IndexPoolingTests
    {

        static uint[] R(params uint[] cells) => cells;

        static SortedWritesTable BuildTable()
        {
            var rows = Enumerable.Range(0, 2000).Select(i => R((uint)i, (uint)(i % 400), (uint)(i % 401))).ToList();
            var table = TableTestUtil.FillTable(rows.Take(1), 1, null, ClosureMerge.TakeNew);
            TableTestUtil.Stage(table, rows.Skip(1).ToArray());
            table.Merge(TableTestUtil.EmptyState());
            return table;
        }

        /// <summary>
        /// Building and disposing a 400-row, 400-value column index over and over should reuse pooled storage after
        /// warm-up, as egglog's pooled <c>IndexMap</c> does; it allocates about 7 KB of fresh hash-table arrays per build.
        /// </summary>
        [Fact]
        public void RepeatedGroupByColumnReusesPooledStorage()
        {
            var table = BuildTable();
            var subset = SubsetRef.Dense(new OffsetRange(0, 400));
            for (var i = 0; i < 50; i++)
                table.GroupByColumn(subset, new ColumnId(1)).Dispose();

            const int builds = 200;
            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var i = 0; i < builds; i++)
            {
                var index = table.GroupByColumn(subset, new ColumnId(1));
                Assert.Equal(400, index.Count);
                index.Dispose();
            }

            var perBuild = (GC.GetAllocatedBytesForCurrentThread() - before) / (double)builds;
            Assert.True(perBuild < 1024, $"each group_by_col over 400 rows allocated {perBuild:F0} bytes after warm-up; egglog reuses pooled maps");
        }

        /// <summary>
        /// The same for a two-column tuple index (egglog rebuilds one on every multi-column <c>get_index</c> call): it
        /// allocates its hash table and its key buffer (with every intermediate growth) afresh on each build.
        /// </summary>
        [Fact]
        public void RepeatedGroupByKeyReusesPooledStorage()
        {
            var table = BuildTable();
            var subset = SubsetRef.Dense(new OffsetRange(0, 400));
            var columns = IndexTestUtil.Ids(1, 2);
            for (var i = 0; i < 50; i++)
                table.GroupByKey(subset, columns).Dispose();

            const int builds = 200;
            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var i = 0; i < builds; i++)
            {
                var index = table.GroupByKey(subset, columns);
                Assert.Equal(400, index.Count);
                index.Dispose();
            }

            var perBuild = (GC.GetAllocatedBytesForCurrentThread() - before) / (double)builds;
            Assert.True(perBuild < 1024, $"each group_by_key over 400 rows allocated {perBuild:F0} bytes after warm-up; egglog reuses pooled hash tables and key buffers");
        }

    }

}

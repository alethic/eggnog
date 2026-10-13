using System;
using System.Collections.Generic;
using System.Linq;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// Helpers shared by the table tests: the counterparts of egglog's <c>table_shortcuts.rs</c> and the dump helpers
    /// of <c>table/tests.rs</c>.
    /// </summary>
    static class TableTestUtil
    {

        /// <summary>
        /// Creates an execution state over an empty catalog: egglog's <c>empty_execution_state!</c>.
        /// </summary>
        /// <returns>The state.</returns>
        public static ExecutionState EmptyState() => new(new TableCatalog());

        /// <summary>
        /// Fills a <see cref="SortedWritesTable"/> with <paramref name="rows"/>, one merge per row so that rows land in
        /// order: egglog's <c>fill_table</c> (core-relations/src/table_shortcuts.rs:28-66).
        /// </summary>
        /// <param name="rows">The rows; at least one.</param>
        /// <param name="keyCount">The number of key columns.</param>
        /// <param name="sortBy">The sort column, if any.</param>
        /// <param name="merge">The merge function.</param>
        /// <param name="parallelism">The parallelism settings.</param>
        /// <returns>The table.</returns>
        public static SortedWritesTable FillTable(IEnumerable<uint[]> rows, int keyCount, ColumnId? sortBy, MergeFunction merge, ParallelHeuristics? parallelism = null)
        {
            var state = EmptyState();
            var list = rows.ToList();
            var table = new SortedWritesTable(keyCount, list[0].Length, sortBy, ReadOnlySpan<ColumnId>.Empty, merge, parallelism ?? ParallelHeuristics.Serial);
            foreach (var row in list)
            {
                var buffer = table.NewBuffer();
                buffer.StageInsert(row);
                buffer.Flush();
                table.Merge(state);
            }

            return table;
        }

        /// <summary>
        /// Scans <paramref name="subset"/> of <paramref name="table"/>: egglog's <c>dump_subset</c>.
        /// </summary>
        /// <param name="table">The table.</param>
        /// <param name="subset">The rows.</param>
        /// <returns>The live rows, with their ids, in scan order.</returns>
        public static List<(int Id, uint[] Row)> DumpSubset(Table table, SubsetRef subset)
        {
            var collector = new RowCollector();
            table.ScanGeneric(subset, ref collector);
            return collector.Rows;
        }

        /// <summary>
        /// Lists the rows of a tagged buffer, stale ones included: egglog's <c>dump_buf</c>.
        /// </summary>
        /// <param name="buffer">The buffer.</param>
        /// <returns>The rows with their tags.</returns>
        public static List<(int Id, uint[] Row)> DumpBuffer(TaggedRowBuffer buffer)
        {
            var result = new List<(int, uint[])>();
            for (var i = 0; i < buffer.Count; i++)
            {
                var row = buffer.GetRow(i, out var id);
                result.Add((id, row.ToArray()));
            }

            return result;
        }

        /// <summary>
        /// Lists every live row of the table, in row order.
        /// </summary>
        /// <param name="table">The table.</param>
        /// <returns>The rows.</returns>
        public static List<uint[]> LiveRows(Table table)
        {
            var all = table.All();
            return DumpSubset(table, all.AsRef()).Select(x => x.Row).ToList();
        }

        /// <summary>
        /// Stages <paramref name="rows"/> into one buffer of <paramref name="table"/> and flushes it.
        /// </summary>
        /// <param name="table">The table.</param>
        /// <param name="rows">The rows.</param>
        public static void Stage(Table table, params uint[][] rows)
        {
            var buffer = table.NewBuffer();
            foreach (var row in rows)
                buffer.StageInsert(row);

            buffer.Flush();
        }

        /// <summary>
        /// Stages removals of <paramref name="keys"/> into one buffer of <paramref name="table"/> and flushes it.
        /// </summary>
        /// <param name="table">The table.</param>
        /// <param name="keys">The keys.</param>
        public static void StageRemove(Table table, params uint[][] keys)
        {
            var buffer = table.NewBuffer();
            foreach (var key in keys)
                buffer.StageRemove(key);

            buffer.Flush();
        }

        /// <summary>
        /// Formats a row for assertion messages.
        /// </summary>
        /// <param name="row">The row.</param>
        /// <returns>The text.</returns>
        public static string Show(uint[] row) => "[" + string.Join(", ", row) + "]";

        /// <summary>
        /// Gets a key of a row as a string usable as a dictionary key.
        /// </summary>
        /// <param name="row">The row.</param>
        /// <param name="keyCount">The number of key columns.</param>
        /// <returns>The key.</returns>
        public static string KeyOf(ReadOnlySpan<uint> row, int keyCount) => string.Join(",", row[..keyCount].ToArray());

    }

}

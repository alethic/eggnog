using System;
using System.Collections.Generic;
using System.Linq;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// Helpers shared by the database tests.
    /// </summary>
    static class DatabaseTestUtil
    {

        /// <summary>
        /// Builds a row.
        /// </summary>
        public static uint[] R(params uint[] cells) => cells;

        /// <summary>
        /// Builds a column id.
        /// </summary>
        public static ColumnId C(int column) => new(column);

        /// <summary>
        /// Stages <paramref name="rows"/> into <paramref name="table"/> through the database, which notifies it, and
        /// flushes the buffer.
        /// </summary>
        public static void Stage(Database db, TableId table, params uint[][] rows)
        {
            var buffer = db.NewBuffer(table);
            foreach (var row in rows)
                buffer.StageInsert(row);

            buffer.Flush();
        }

        /// <summary>
        /// Stages removals of <paramref name="keys"/> from <paramref name="table"/> through the database and flushes.
        /// </summary>
        public static void StageRemove(Database db, TableId table, params uint[][] keys)
        {
            var buffer = db.NewBuffer(table);
            foreach (var key in keys)
                buffer.StageRemove(key);

            buffer.Flush();
        }

        /// <summary>
        /// Lists the live rows of a table, sorted lexicographically.
        /// </summary>
        public static List<uint[]> Rows(Database db, TableId table)
        {
            var rows = TableTestUtil.LiveRows(db.GetTable(table));
            rows.Sort(CompareRows);
            return rows;
        }

        /// <summary>
        /// Formats the live rows of a table, sorted, for comparisons.
        /// </summary>
        public static string Dump(Database db, TableId table) => string.Join(" ", Rows(db, table).Select(TableTestUtil.Show));

        /// <summary>
        /// Lists the live rows of <paramref name="subset"/> of a table, sorted.
        /// </summary>
        public static List<uint[]> Rows(Table table, SubsetRef subset)
        {
            var rows = TableTestUtil.DumpSubset(table, subset).Select(x => x.Row).ToList();
            rows.Sort(CompareRows);
            return rows;
        }

        /// <summary>
        /// Compares rows lexicographically.
        /// </summary>
        public static int CompareRows(uint[] a, uint[] b)
        {
            for (var i = 0; i < Math.Min(a.Length, b.Length); i++)
            {
                var c = a[i].CompareTo(b[i]);
                if (c != 0)
                    return c;
            }

            return a.Length.CompareTo(b.Length);
        }

        /// <summary>
        /// Creates a sorted-writes function table <c>[keys..., value, ts]</c> sorted by its timestamp column.
        /// </summary>
        public static SortedWritesTable Function(int keyCount, MergeFunction merge, ReadOnlySpan<ColumnId> toRebuild = default, ParallelHeuristics? parallelism = null)
        {
            return new SortedWritesTable(keyCount, keyCount + 2, C(keyCount + 1), toRebuild, merge, parallelism ?? ParallelHeuristics.Serial);
        }

    }

}

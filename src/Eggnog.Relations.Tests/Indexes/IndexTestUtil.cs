using System;
using System.Collections.Generic;
using System.Linq;

using Xunit;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// Helpers shared by the index tests: a naive group-by over a table's live rows, the reference every index is
    /// checked against, and converters from index entries to plain collections.
    /// </summary>
    static class IndexTestUtil
    {

        /// <summary>
        /// Copies the row ids of a subset.
        /// </summary>
        /// <param name="rows">The subset.</param>
        /// <returns>The row ids in order.</returns>
        public static int[] ToArray(SubsetRef rows)
        {
            var result = new List<int>();
            foreach (var row in rows)
                result.Add(row);

            return result.ToArray();
        }

        /// <summary>
        /// Formats a tuple key as a dictionary key.
        /// </summary>
        /// <param name="key">The key.</param>
        /// <returns>The text.</returns>
        public static string KeyOf(ReadOnlySpan<uint> key) => string.Join(",", key.ToArray());

        /// <summary>
        /// Groups the live rows of <paramref name="table"/> by each distinct value of <paramref name="columns"/>: what a
        /// (multi-column) <see cref="ColumnIndex"/> must list, ignoring tombstones.
        /// </summary>
        /// <param name="table">The table.</param>
        /// <param name="columns">The covered columns.</param>
        /// <returns>The sorted rows of each value.</returns>
        public static Dictionary<uint, int[]> GroupByValue(Table table, params int[] columns)
        {
            var result = new Dictionary<uint, SortedSet<int>>();
            var all = table.All();
            foreach (var (id, row) in TableTestUtil.DumpSubset(table, all.AsRef()))
            {
                foreach (var column in columns)
                {
                    if (result.TryGetValue(row[column], out var set) == false)
                        result[row[column]] = set = new SortedSet<int>();

                    set.Add(id);
                }
            }

            return result.ToDictionary(x => x.Key, x => x.Value.ToArray());
        }

        /// <summary>
        /// Groups the live rows of <paramref name="table"/> by the tuple of <paramref name="columns"/>: what a
        /// <see cref="TupleIndex"/> must list, ignoring tombstones.
        /// </summary>
        /// <param name="table">The table.</param>
        /// <param name="columns">The key columns.</param>
        /// <returns>The sorted rows of each key.</returns>
        public static Dictionary<string, int[]> GroupByTuple(Table table, params int[] columns)
        {
            var all = table.All();
            return TableTestUtil.DumpSubset(table, all.AsRef())
                .GroupBy(x => KeyOf(columns.Select(c => x.Row[c]).ToArray()))
                .ToDictionary(g => g.Key, g => g.Select(x => x.Id).OrderBy(x => x).ToArray());
        }

        /// <summary>
        /// Keeps the rows of <paramref name="rows"/> that are live in <paramref name="table"/>.
        /// </summary>
        /// <param name="table">The table.</param>
        /// <param name="rows">Row ids.</param>
        /// <returns>The live ones.</returns>
        public static int[] Live(SortedWritesTable table, int[] rows) => rows.Where(r => table.Rows.IsStale(r) == false).ToArray();

        /// <summary>
        /// Asserts that a subset is strictly ascending: sorted, without duplicates.
        /// </summary>
        /// <param name="rows">The row ids.</param>
        /// <param name="what">What is checked, for the message.</param>
        public static void AssertStrictlyAscending(int[] rows, string what)
        {
            for (var i = 1; i < rows.Length; i++)
                Assert.True(rows[i - 1] < rows[i], $"{what}: rows {string.Join(",", rows)} are not strictly ascending");
        }

        /// <summary>
        /// Checks that the live part of every entry of <paramref name="actual"/> is exactly the group of
        /// <paramref name="expected"/> with that key, that every expected group is present, and that every entry is
        /// strictly ascending, nonempty and within the table. Entries may also list rows deleted since they were
        /// indexed.
        /// </summary>
        /// <typeparam name="TKey">The key type.</typeparam>
        /// <param name="table">The table.</param>
        /// <param name="expected">The naive group-by of the live rows.</param>
        /// <param name="actual">The index's entries.</param>
        /// <param name="what">What is checked, for messages.</param>
        public static void AssertCoversLiveRows<TKey>(SortedWritesTable table, Dictionary<TKey, int[]> expected, Dictionary<TKey, int[]> actual, string what)
            where TKey : notnull
        {
            foreach (var (key, rows) in actual)
            {
                Assert.NotEmpty(rows);
                AssertStrictlyAscending(rows, $"{what} key {key}");
                Assert.All(rows, r => Assert.InRange(r, 0, table.Rows.Count - 1));
                var live = Live(table, rows);
                if (expected.TryGetValue(key, out var want))
                    Assert.True(want.SequenceEqual(live), $"{what} key {key}: live rows [{string.Join(",", live)}], expected [{string.Join(",", want)}]");
                else
                    Assert.True(live.Length == 0, $"{what} key {key}: lists live rows [{string.Join(",", live)}] for a key no live row has");
            }

            foreach (var key in expected.Keys)
                Assert.True(actual.ContainsKey(key), $"{what}: key {key} is missing");
        }

        /// <summary>
        /// Checks that <paramref name="actual"/> lists exactly the groups of <paramref name="expected"/>: what an index
        /// built from scratch must hold, since scans skip tombstones.
        /// </summary>
        /// <typeparam name="TKey">The key type.</typeparam>
        /// <param name="expected">The naive group-by of the live rows.</param>
        /// <param name="actual">The index's entries.</param>
        /// <param name="what">What is checked, for messages.</param>
        public static void AssertExactly<TKey>(Dictionary<TKey, int[]> expected, Dictionary<TKey, int[]> actual, string what)
            where TKey : notnull
        {
            Assert.True(expected.Count == actual.Count, $"{what}: {actual.Count} keys, expected {expected.Count}");
            foreach (var (key, rows) in expected)
            {
                Assert.True(actual.TryGetValue(key, out var got), $"{what}: key {key} is missing");
                Assert.True(rows.SequenceEqual(got!), $"{what} key {key}: rows [{string.Join(",", got!)}], expected [{string.Join(",", rows)}]");
            }
        }

        /// <summary>
        /// Collects the entries of a column index.
        /// </summary>
        /// <param name="index">The index.</param>
        /// <returns>The rows of each key.</returns>
        public static Dictionary<uint, int[]> Entries(ColumnIndex index)
        {
            var collector = new ColumnIndexCollector();
            index.ForEach(ref collector);
            return collector.Entries;
        }

        /// <summary>
        /// Collects the entries of a tuple index.
        /// </summary>
        /// <param name="index">The index.</param>
        /// <returns>The rows of each key.</returns>
        public static Dictionary<string, int[]> Entries(TupleIndex index)
        {
            var collector = new TupleIndexCollector();
            index.ForEach(ref collector);
            return collector.Entries;
        }

        /// <summary>
        /// Converts column numbers to <see cref="ColumnId"/>s.
        /// </summary>
        /// <param name="columns">The columns.</param>
        /// <returns>The ids.</returns>
        public static ColumnId[] Ids(params int[] columns) => columns.Select(c => new ColumnId(c)).ToArray();

    }

}

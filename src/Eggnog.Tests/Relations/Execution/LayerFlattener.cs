using System;
using System.Collections.Generic;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// What a layered view shows, as one flat table (colored-contexts spec §17 M10.2, T8): the rows of every present layer
    /// that are live and that the view does not hide. The oracle side of the layered executor's property tests: a join of
    /// the visible rows (a nested loop, or the flat executor over <see cref="Flatten"/>'s database) must give the layered
    /// executor's distinct bindings (I27).
    /// </summary>
    static class LayerFlattener
    {

        /// <summary>
        /// Lists the rows <paramref name="view"/> shows, layer by layer from the bottom.
        /// </summary>
        public static List<uint[]> VisibleRows(TableView view)
        {
            var rows = new List<uint[]>();
            for (var d = 0; d < view.LayerCount; d++)
            {
                if (view.Layers[d] is not { } layer)
                    continue;

                var table = (SortedWritesTable)layer.Table;
                var data = table.Rows;
                for (var r = 0; r < data.Count; r++)
                    if (data.IsStale(r) == false && layer.Hidden.Contains(r) == false)
                        rows.Add(data.GetRow(r).ToArray());
            }

            return rows;
        }

        /// <summary>
        /// Builds a bare database holding, at each id of <paramref name="tables"/>, a table of the same shape with exactly
        /// the rows <paramref name="views"/> shows of it; a view must show at most one row per key (S6), as a context's
        /// does. Rows are merged in timestamp order, one merge per stamp, so a sorted column stays sorted.
        /// </summary>
        /// <param name="tables">The tables, in id order from 0, each with its shape: key count, arity and sort column.</param>
        /// <param name="views">The views.</param>
        /// <param name="mode">The mode the flat database runs rules in (<see cref="ExecutionTestUtil.Modes"/>).</param>
        /// <returns>The flat database.</returns>
        /// <exception cref="InvalidOperationException">A view shows two rows with one key.</exception>
        public static Database Flatten((int KeyCount, int Arity, int SortColumn)[] tables, Func<TableId, TableView> views, string mode)
        {
            var db = ExecutionTestUtil.NewDatabase(mode);
            for (var i = 0; i < tables.Length; i++)
            {
                var (keys, arity, sort) = tables[i];
                var id = db.AddTable(new SortedWritesTable(keys, arity, sort >= 0 ? new ColumnId(sort) : null, ReadOnlySpan<ColumnId>.Empty, ClosureMerge.TakeNew, ParallelHeuristics.Serial));
                var rows = VisibleRows(views(id));
                var seen = new HashSet<string>(StringComparer.Ordinal);
                foreach (var row in rows)
                    if (seen.Add(TableTestUtil.KeyOf(row, keys)) == false)
                        throw new InvalidOperationException($"The view of table {id} shows two rows with key [{TableTestUtil.KeyOf(row, keys)}]; it cannot be flattened into a keyed table.");

                if (sort >= 0)
                    rows.Sort((a, b) => a[sort].CompareTo(b[sort]));

                var start = 0;
                while (start < rows.Count)
                {
                    var end = start + 1;
                    while (sort >= 0 && end < rows.Count && rows[end][sort] == rows[start][sort])
                        end++;

                    if (sort < 0)
                        end = rows.Count;

                    DatabaseTestUtil.Stage(db, id, rows.GetRange(start, end - start).ToArray());
                    db.MergeAll();
                    start = end;
                }
            }

            return db;
        }

    }

}

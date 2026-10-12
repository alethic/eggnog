using System;
using System.Collections.Generic;
using System.Linq;

using Xunit;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// The <c>LookupPathsAgree</c> comparison (colored-contexts spec §12.7, I12, I18): for every table of a clean context
    /// and every key that any layer of its view holds (plus extra probes), the write-phase lookup
    /// (<see cref="ContextTables.TryFindVisible"/>), the read-phase lookup (<see cref="TableView.FindRow(ReadOnlySpan{uint}, out int)"/>)
    /// and a filtered scan of the view's layers agree on whether a row is visible and which; at most one visible row holds
    /// a key; the view's visible count is exact; and no hidden set is left unsealed. Run after every write.
    /// </summary>
    static class LookupPaths
    {

        /// <summary>
        /// Checks every table of <paramref name="context"/> and returns what it sees, by table and key.
        /// </summary>
        /// <param name="context">A clean context.</param>
        /// <param name="probes">Extra keys to probe, by key width.</param>
        /// <returns>The visible rows, by table, by key (<see cref="LayeredTestUtil.KeyOf"/>).</returns>
        public static Dictionary<TableId, Dictionary<string, uint[]>> Check(Context context, IEnumerable<uint[]>? probes = null)
        {
            var schema = context.Store.Schema;
            if (context.Tables is { } tables)
                Assert.Equal(0, tables.UnsealedCount);

            var extra = probes?.ToList() ?? new List<uint[]>();
            var seen = new Dictionary<TableId, Dictionary<string, uint[]>>();
            foreach (var table in schema.FunctionTables.ToArray())
                seen[table] = CheckTable(context, table, extra);

            return seen;
        }

        /// <summary>
        /// Checks one table.
        /// </summary>
        static Dictionary<string, uint[]> CheckTable(Context context, TableId table, List<uint[]> probes)
        {
            var spec = context.Store.Schema.GetSpec(table);
            var keyCount = spec.KeyCount;
            var view = context.GetView(table);

            // The filtered scan: one visible row per key (I12).
            var visible = new Dictionary<string, (int Layer, int Row, uint[] Cells)>();
            foreach (var (layer, row, cells) in LayeredTestUtil.ScanVisible(view))
            {
                var key = LayeredTestUtil.KeyOf(cells.AsSpan(0, keyCount));
                Assert.False(visible.ContainsKey(key), $"two visible rows hold key [{key}] of table {table} (I12)");
                visible[key] = (layer, row, cells);
            }

            Assert.Equal(visible.Count, view.VisibleCount);

            // Every key any layer holds, visible or not, and the probes of the table's width.
            var keys = new Dictionary<string, uint[]>();
            for (var j = 0; j < view.LayerCount; j++)
            {
                if (view.Layers[j] is not { } layer)
                    continue;

                foreach (var row in TableTestUtil.LiveRows(layer.Table))
                    keys.TryAdd(LayeredTestUtil.KeyOf(row.AsSpan(0, keyCount)), row[..keyCount]);
            }

            foreach (var probe in probes)
                if (probe.Length == keyCount)
                    keys.TryAdd(LayeredTestUtil.KeyOf(probe), probe);

            if (keyCount == 0)
                keys.TryAdd(string.Empty, Array.Empty<uint>());

            var destination = new uint[spec.Arity];
            var viaContext = new uint[spec.Arity];
            foreach (var (text, key) in keys)
            {
                var readRow = view.FindRow(key, out var readLayer);
                var found = visible.TryGetValue(text, out var scanned);
                Assert.Equal(found, readRow >= 0);
                if (found)
                {
                    Assert.Equal(scanned.Layer, readLayer);
                    Assert.Equal(scanned.Row, readRow);
                }

                // The write phase: the context's layering, or the root's table.
                bool written;
                int writeLayer;
                int writeRow;
                if (context.Tables is { } tables)
                {
                    var array = key.Length == 0 ? new uint[1] : (uint[])key.Clone();
                    written = tables.TryFindVisible(table, array, 0, TableKeyHash.Hash(array, 0, keyCount), tables.Depth, out _, out writeLayer, out writeRow);
                    Assert.Equal(found, tables.TryGetVisibleRow(table, key, destination));
                    if (found)
                        Assert.Equal(scanned.Cells, destination);
                }
                else
                {
                    writeRow = context.Store.Schema[table]!.Root.FindRow(key);
                    writeLayer = writeRow >= 0 ? 0 : -1;
                    written = writeRow >= 0;
                }

                Assert.Equal(found, written);
                if (found)
                {
                    Assert.Equal(scanned.Layer, writeLayer);
                    Assert.Equal(scanned.Row, writeRow);
                }

                // The context's own read API and the view's row copy.
                Assert.Equal(found, context.TryGetRow(table, key, viaContext));
                Assert.Equal(found, view.TryGetRow(key, destination, out _, out _));
                if (found)
                {
                    Assert.Equal(scanned.Cells, viaContext);
                    Assert.Equal(scanned.Cells, destination);
                    for (var c = 0; c < spec.Arity; c++)
                    {
                        Assert.True(view.TryGetColumn(key, c, out var cell));
                        Assert.Equal(scanned.Cells[c], cell);
                    }
                }
            }

            return visible.ToDictionary(pair => pair.Key, pair => pair.Value.Cells);
        }

    }

}

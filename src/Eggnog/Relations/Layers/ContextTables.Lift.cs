using System.Collections.Generic;

namespace Eggnog.Relations
{

    internal sealed partial class ContextTables
    {

        /// <summary>
        /// Visits every live row of the context's own layer of <paramref name="table"/>: the rows the context stored
        /// itself, canonical in it. Nothing when it has no layer. O(own rows).
        /// </summary>
        /// <typeparam name="TSink">The sink type.</typeparam>
        /// <param name="table">A registered function table.</param>
        /// <param name="sink">Receives each row with its row id in the own layer.</param>
        internal void ScanOwn<TSink>(TableId table, ref TSink sink)
            where TSink : struct, IRowSink
        {
            if (TryGetLayer(table) is not { } layer)
                return;

            var own = layer.Own;
            var all = own.All();
            own.ScanGeneric(all.AsRef(), ref sink);
            all.Dispose();
        }

        /// <summary>
        /// Visits every ancestor row of <paramref name="table"/> the context sees that names <paramref name="id"/> in
        /// an id column (the winner probe of the lift): through each visible ancestor layer's rebuild index, never a
        /// scan. Rows come layer by layer from the root up, each layer's in row order, each once.
        /// </summary>
        /// <remarks>
        /// Nothing for a table without rebuild columns (no id can be named), for a context-private table (ancestors'
        /// layers of it are not visible), or for an id local to the context (no ancestor row names one). The id must be
        /// canonical in the context. Cost: O(depth) index probes plus the uses of the id.
        /// </remarks>
        /// <typeparam name="TSink">The sink type.</typeparam>
        /// <param name="table">A registered function table.</param>
        /// <param name="id">An id canonical in the context.</param>
        /// <param name="sink">Receives each row with its row id in its layer.</param>
        /// <exception cref="KeyNotFoundException">The table is not registered.</exception>
        internal void ScanAncestorsMentioning<TSink>(TableId table, uint id, ref TSink sink)
            where TSink : struct, IRowSink
        {
            var s = schema[table] ?? throw new KeyNotFoundException($"Table {table} is not registered with the store.");
            if (s.IsContextPrivate || s.RebuildColumns.Length == 0 || owner.IdBlocks!.Contains(id))
                return;

            for (var j = ClearedFloor(table); j < depth; j++)
            {
                if (AncestorLayer(j, table) is not { Count: > 0 } layer)
                    continue;

                var gather = new ColumnIndexGatherSink(layer.SharedRebuildIndex()!.Data);
                gather.AddValue(id);
                var count = gather.SortUnique();
                var rows = gather.Rows;
                for (var i = 0; i < count; i++)
                {
                    var r = rows[i];
                    if (layer.Rows.IsStale(r) || HiddenBetween(table, j, r))
                        continue;

                    sink.AddRow(r, layer.Rows.GetRow(r));
                }

                gather.Release();
            }
        }

    }

}

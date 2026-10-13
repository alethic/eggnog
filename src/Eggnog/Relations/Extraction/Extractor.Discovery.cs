namespace Eggnog.Relations
{

    internal sealed partial class Extractor
    {

        /// <summary>
        /// Reads every row of every function in the extractor's slots, in slot order: the rows egglog's
        /// <c>bellman_ford</c> relaxes (src/extract.rs:415-458). Subsumed rows are skipped. O(rows).
        /// </summary>
        void ScanAll()
        {
            for (var slot = 0; slot < slots.Length; slot++)
            {
                var visitor = new ExtractionScanVisitor(this, slot);
                egraph.ForEach(slots[slot].Id, ref visitor);
            }
        }

        /// <summary>
        /// Discovers every class queued since the last call, and the classes they reach, then runs Knuth's algorithm over
        /// what was added, replaying the finalizations from earlier queries that decide ties among the new nodes
        /// (Extractor.Replay.cs), so that the result is a from-scratch run's over everything discovered. Each class's
        /// nodes are the rows the context sees of the functions producing its sort whose output column holds it
        /// (<see cref="DiscoverAtRoot"/>, <see cref="DiscoverInLayers"/>), and the classes among their children are
        /// queued in turn, so the graph stays closed downward. O(rows found + occurrences + index probes + replayed
        /// choices), plus the first build of each index.
        /// </summary>
        void Settle()
        {
            run++;
            if (layers is null)
                DiscoverAtRoot();
            else
                DiscoverInLayers(layers);

            RunKnuth();
        }

        /// <summary>
        /// Discovers the queued classes at the root: each producing function's rows are found through the root database's
        /// cached index on the function's output column (built on first use, then kept current by the database).
        /// O(rows found + index probes).
        /// </summary>
        void DiscoverAtRoot()
        {
            var database = egraph.Database;
            while (undiscovered.TryDequeue(out var found))
            {
                var value = classValue[found];
                foreach (var slot in sortProducers[classSort[found]])
                {
                    var function = slots[slot];
                    var info = database.GetTableInfo(function.Table);
                    var index = info.GetColumnIndex(new ColumnId(function.OutputIndex));
                    if (index.Data.TryGetSubset(value, out var rows) == false)
                        continue;

                    var sink = new ExtractionRowSink(this, slot, function.Math);
                    info.Table.ScanGeneric(rows, ref sink);
                }
            }
        }

        /// <summary>
        /// Discovers the queued classes in a child context (colored-contexts spec §10.10): for each producing function,
        /// every present layer of the context's view is probed through that layer's own cached index on the output column
        /// (shared and lock-free on a frozen layer, §9.5), and the rows found are scanned with the layer's hidden rows
        /// skipped (§9.3), so exactly the rows the context sees are read (I12): an ancestor's row the context replaced, or
        /// that names one of its own losers, is hidden, and its canonical copy lies in a later layer. The view is never
        /// read through <see cref="Database.GetTable"/>. O(layers × index probes + rows found), plus the first build of
        /// each layer's index.
        /// </summary>
        /// <param name="tables">The context's layers.</param>
        void DiscoverInLayers(ContextTables tables)
        {
            while (undiscovered.TryDequeue(out var found))
            {
                var value = classValue[found];
                foreach (var slot in sortProducers[classSort[found]])
                {
                    var function = slots[slot];
                    var column = new ColumnId(function.OutputIndex);
                    var view = tables.GetView(function.Table);
                    var present = view.Layers;
                    for (var l = 0; l < present.Length; l++)
                    {
                        if (present[l] is not { } layer)
                            continue;

                        var index = layer.Info.GetColumnIndex(column);
                        if (index.Data.TryGetSubset(value, out var rows) == false)
                            continue;

                        var sink = new ExtractionRowSink(this, slot, function.Math);
                        layer.Table.ScanGeneric(rows, layer.Hidden, ref sink);
                    }
                }
            }
        }

    }

}

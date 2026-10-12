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
        /// nodes are the rows of the functions producing its sort whose output column holds it, found through the
        /// database's cached index on that column (built on first use, then kept current by the database), and the
        /// classes among their children are queued in turn, so the graph stays closed downward. O(rows found +
        /// occurrences + index probes + replayed choices), plus the first build of each index.
        /// </summary>
        void Settle()
        {
            run++;
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

            RunKnuth();
        }

    }

}

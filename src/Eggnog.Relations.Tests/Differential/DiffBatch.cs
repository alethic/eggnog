using System.Collections.Generic;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// The writes staged into the differential model between two merges: per table, removals and inserts in staging
    /// order; inserts into the union-find are unions. <see cref="DiffModel.Merge"/> applies a batch as the engine's
    /// <see cref="Database.MergeAll"/> would.
    /// </summary>
    sealed class DiffBatch
    {

        readonly SortedDictionary<int, (List<uint[]> Removals, List<uint[]> Inserts)> tables = new();

        /// <summary>
        /// Gets the staged tables in ascending order with their writes.
        /// </summary>
        public IEnumerable<KeyValuePair<int, (List<uint[]> Removals, List<uint[]> Inserts)>> Tables => tables;

        /// <summary>
        /// Stages the insertion of <paramref name="row"/> into <paramref name="table"/>.
        /// </summary>
        public void Insert(int table, uint[] row) => Get(table).Inserts.Add(row);

        /// <summary>
        /// Stages the removal of <paramref name="key"/> from <paramref name="table"/>.
        /// </summary>
        public void Remove(int table, uint[] key) => Get(table).Removals.Add(key);

        (List<uint[]> Removals, List<uint[]> Inserts) Get(int table)
        {
            if (tables.TryGetValue(table, out var entry) == false)
                tables.Add(table, entry = (new List<uint[]>(), new List<uint[]>()));

            return entry;
        }

    }

}

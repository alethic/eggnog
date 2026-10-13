using System;
using System.Collections.Generic;
using System.Linq;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// A reference implementation of a <see cref="SortedWritesTable"/>'s contents: a dictionary from key to row with
    /// egglog's serial merge semantics (map §2.3, "the conflict contract"). A batch applies every removal first, then
    /// folds the inserts of each key left to right, starting from the stored row: <c>r := stored; for each incoming q,
    /// if merge(r, q) then r := merge(r, q)</c>; with no stored row the first incoming row starts the fold.
    /// </summary>
    sealed class SortedWritesModel
    {

        readonly int keyCount;
        readonly Dictionary<string, uint[]> rows = new();

        /// <summary>
        /// Initializes an empty model.
        /// </summary>
        /// <param name="keyCount">The number of key columns.</param>
        public SortedWritesModel(int keyCount)
        {
            this.keyCount = keyCount;
        }

        /// <summary>
        /// Gets the number of rows.
        /// </summary>
        public int Count => rows.Count;

        /// <summary>
        /// Gets the rows.
        /// </summary>
        public IEnumerable<uint[]> Rows => rows.Values;

        /// <summary>
        /// Removes every row.
        /// </summary>
        public void Clear() => rows.Clear();

        /// <summary>
        /// Applies one merge batch.
        /// </summary>
        /// <param name="removals">The keys removed, in staging order.</param>
        /// <param name="inserts">The rows inserted, in processing order.</param>
        /// <param name="merge">The merge: (current, incoming) to the new row, or <c>null</c> to keep the current one.</param>
        /// <returns>What a table reports for the batch.</returns>
        public TableChange Apply(IEnumerable<uint[]> removals, IEnumerable<uint[]> inserts, Func<uint[], uint[], uint[]?> merge)
        {
            var removed = false;
            foreach (var key in removals)
                removed |= rows.Remove(TableTestUtil.KeyOf(key, keyCount));

            var added = false;
            foreach (var row in inserts)
            {
                var key = TableTestUtil.KeyOf(row, keyCount);
                if (rows.TryGetValue(key, out var current) == false)
                {
                    rows[key] = row.ToArray();
                    added = true;
                    continue;
                }

                var result = merge(current, row);
                if (result is null)
                    continue;

                rows[key] = result;
                added = true;
            }

            return new TableChange(added, removed);
        }

        /// <summary>
        /// Gets the row with the key of <paramref name="key"/>, if any.
        /// </summary>
        /// <param name="key">The key, or a row starting with it.</param>
        /// <param name="row">The row.</param>
        /// <returns><c>true</c> if present.</returns>
        public bool TryGet(ReadOnlySpan<uint> key, out uint[] row) => rows.TryGetValue(TableTestUtil.KeyOf(key, keyCount), out row!);

    }

}

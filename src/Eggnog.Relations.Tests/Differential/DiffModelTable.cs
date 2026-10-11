using System;
using System.Collections.Generic;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// The differential model of one <see cref="SortedWritesTable"/>: a dictionary from key to row with the table's
    /// merge semantics, and nothing else (no tombstones, runs, shards or indexes).
    /// </summary>
    /// <remarks>
    /// A batch applies as the engine's <see cref="SortedWritesTable.Merge"/> documents: every staged removal first, then
    /// every staged insert in staging order, each one either adding its key or going through the merge function against
    /// the row the key has now (a left fold). The merge semantics are restated here from the engine's merge functions'
    /// contracts, not called.
    /// </remarks>
    sealed class DiffModelTable
    {

        readonly Dictionary<uint[], uint[]> rows;

        /// <summary>
        /// Initializes an empty table.
        /// </summary>
        public DiffModelTable(DiffTable table)
        {
            Table = table;
            rows = new Dictionary<uint[], uint[]>(DiffRowComparer.Instance);
        }

        /// <summary>
        /// Gets the declaration.
        /// </summary>
        public DiffTable Table { get; }

        /// <summary>
        /// Lists the rows (the arrays are shared; callers must not modify them).
        /// </summary>
        public List<uint[]> Rows() => new(rows.Values);

        /// <summary>
        /// Gets the row with key <paramref name="key"/>, or <c>null</c>.
        /// </summary>
        public uint[]? Get(uint[] key) => rows.TryGetValue(key, out var row) ? row : null;

        /// <summary>
        /// Gets the key of <paramref name="row"/>.
        /// </summary>
        public uint[] KeyOf(uint[] row) => row.AsSpan(0, Table.KeyCount).ToArray();

        /// <summary>
        /// Applies one batch: <paramref name="removals"/> first, then <paramref name="inserts"/> in order.
        /// </summary>
        /// <param name="removals">The keys to remove.</param>
        /// <param name="inserts">The rows to insert.</param>
        /// <param name="unions">Receives the unions <see cref="DiffMergeKind.UnionId"/> stages, as <c>[a, b, ts]</c>.</param>
        /// <param name="epoch">The merge state's timestamp.</param>
        /// <param name="staged">Set when a merge function staged a write (the engine's
        /// <see cref="ExecutionState.Changed"/>).</param>
        /// <returns>Whether a key was added or a merge changed a row (the engine's <see cref="TableChange.Added"/>).</returns>
        public bool Apply(List<uint[]> removals, List<uint[]> inserts, List<uint[]> unions, uint epoch, ref bool staged)
        {
            foreach (var key in removals)
                rows.Remove(key);

            var added = false;
            foreach (var row in inserts)
            {
                var key = KeyOf(row);
                if (rows.TryGetValue(key, out var current) == false)
                {
                    rows.Add(key, row);
                    added = true;
                }
                else if (Merge(current, row, unions, epoch, ref staged) is { } merged)
                {
                    rows[key] = merged;
                    added = true;
                }
            }

            return added;
        }

        /// <summary>
        /// Merges <paramref name="incoming"/> into <paramref name="current"/>.
        /// </summary>
        /// <returns>The new row, or <c>null</c> to keep the current one.</returns>
        uint[]? Merge(uint[] current, uint[] incoming, List<uint[]> unions, uint epoch, ref bool staged)
        {
            var v = Table.ValueColumn;
            switch (Table.Merge)
            {
                case DiffMergeKind.TakeNew:
                    return incoming;
                case DiffMergeKind.KeepCurrent:
                    return null;
                case DiffMergeKind.KeepIncoming:
                    return incoming[v] == current[v] ? null : incoming;
                case DiffMergeKind.Min:
                    return incoming[v] < current[v] ? incoming : null;
                case DiffMergeKind.Max:
                    return incoming[v] > current[v] ? incoming : null;
                case DiffMergeKind.UnionId:
                    if (incoming[v] == current[v])
                        return null;

                    unions.Add([current[v], incoming[v], Table.UnionUsesTimestamp && Table.IsSorted ? incoming[Table.TimestampColumn] : epoch]);
                    staged = true;
                    return incoming[v] < current[v] ? incoming : null;
                default:
                    throw new InvalidOperationException("Unknown merge " + Table.Merge);
            }
        }

    }

}

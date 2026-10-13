using System;
using System.Collections.Generic;
using System.Linq;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// A naive e-graph: a dictionary union-find whose canonical id is a class's minimum, and a list of logical rows per
    /// table, closed by rewriting every row and grouping by key until no group merges anything; plus retraction of a
    /// key and of a table, applied to the closed view.
    /// </summary>
    sealed class ReferenceContextView
    {

        readonly Dictionary<ulong, ulong> parents = new();
        readonly Dictionary<LogicalTable, List<ulong[]>> rows = new();

        /// <summary>
        /// Initializes an empty view over <paramref name="tables"/>.
        /// </summary>
        public ReferenceContextView(IReadOnlyList<LogicalTable> tables)
        {
            foreach (var table in tables)
                rows[table] = new List<ulong[]>();
        }

        /// <summary>
        /// Gets the canonical id of <paramref name="id"/>: the minimum of its class.
        /// </summary>
        public ulong Find(ulong id)
        {
            while (parents.TryGetValue(id, out var next))
                id = next;

            return id;
        }

        /// <summary>
        /// Merges the classes of <paramref name="a"/> and <paramref name="b"/>; the smaller canonical id wins.
        /// </summary>
        /// <returns><c>true</c> if they were distinct.</returns>
        public bool Union(ulong a, ulong b)
        {
            a = Find(a);
            b = Find(b);
            if (a == b)
                return false;

            parents[Math.Max(a, b)] = Math.Min(a, b);
            return true;
        }

        /// <summary>
        /// Adds a logical row.
        /// </summary>
        public void Add(LogicalTable table, ulong[] row) => rows[table].Add((ulong[])row.Clone());

        /// <summary>
        /// Closes the view, then removes the row whose canonical key is the canonical form of <paramref name="key"/>.
        /// </summary>
        public void Delete(LogicalTable table, ulong[] key)
        {
            Close();
            var probe = Canonical(table, key, table.KeyCount);
            rows[table].RemoveAll(row => row.AsSpan(0, table.KeyCount).SequenceEqual(probe));
        }

        /// <summary>
        /// Closes the view, then removes every row of <paramref name="table"/>.
        /// </summary>
        public void Clear(LogicalTable table)
        {
            Close();
            rows[table].Clear();
        }

        /// <summary>
        /// Gets the rows of <paramref name="table"/>; canonical once the view is closed.
        /// </summary>
        public IReadOnlyList<ulong[]> Rows(LogicalTable table) => rows[table];

        /// <summary>
        /// Looks up the row with the canonical form of <paramref name="key"/> in the closed view.
        /// </summary>
        public bool TryLookup(LogicalTable table, ReadOnlySpan<ulong> key, out ulong output)
        {
            var probe = Canonical(table, key.ToArray(), table.KeyCount);
            foreach (var row in rows[table])
            {
                if (row.AsSpan(0, table.KeyCount).SequenceEqual(probe))
                {
                    output = table.HasOutput ? row[table.KeyCount] : 0;
                    return true;
                }
            }

            output = 0;
            return false;
        }

        /// <summary>
        /// Rewrites every row canonically and groups rows by key, merging outputs (ids by union, values by the table's
        /// merge), until nothing merges.
        /// </summary>
        public void Close()
        {
            bool merged;
            do
            {
                merged = false;
                foreach (var table in rows.Keys.ToList())
                {
                    var groups = new Dictionary<string, ulong[]>();
                    foreach (var original in rows[table])
                    {
                        var row = Canonical(table, original, original.Length);
                        var key = string.Join(",", row.Take(table.KeyCount));
                        if (groups.TryGetValue(key, out var existing) == false)
                        {
                            groups[key] = row;
                            continue;
                        }

                        if (table.HasOutput == false)
                            continue;

                        if (table.OutputIsId)
                            merged |= Union(existing[table.KeyCount], row[table.KeyCount]);
                        else
                            existing[table.KeyCount] = table.MergeValues(existing[table.KeyCount], row[table.KeyCount]);
                    }

                    rows[table] = groups.Values.ToList();
                }
            }
            while (merged);

            // A final rewrite, so every output is canonical after the last union.
            foreach (var table in rows.Keys.ToList())
                rows[table] = rows[table].Select(row => Canonical(table, row, row.Length)).ToList();
        }

        /// <summary>
        /// Copies the first <paramref name="length"/> cells of <paramref name="row"/> with every id made canonical.
        /// </summary>
        ulong[] Canonical(LogicalTable table, ulong[] row, int length)
        {
            var copy = new ulong[length];
            for (var column = 0; column < length; column++)
                copy[column] = table.IsId[column] ? Find(row[column]) : row[column];

            return copy;
        }

    }

}

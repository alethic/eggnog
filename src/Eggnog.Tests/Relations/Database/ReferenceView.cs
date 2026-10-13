using System;
using System.Collections.Generic;
using System.Linq;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// A naive e-graph: a dictionary union-find and a list of rows per table, closed by rewriting every row and
    /// grouping by key until no group merges anything, over the engine's 32-bit cells.
    /// </summary>
    /// <remarks>
    /// Nothing here is incremental, shared or clever, which is the point: it defines what the database should hold
    /// after merging and rebuilding, and the database is tested by agreeing with it modulo the choice of canonical ids.
    /// </remarks>
    sealed class ReferenceView
    {

        readonly Dictionary<uint, uint> parents = new();
        readonly Dictionary<ReferenceTable, List<uint[]>> rows = new();

        /// <summary>
        /// Initializes an empty view over <paramref name="tables"/>.
        /// </summary>
        public ReferenceView(IReadOnlyList<ReferenceTable> tables)
        {
            foreach (var table in tables)
                rows[table] = new List<uint[]>();
        }

        /// <summary>
        /// Finds the representative of <paramref name="id"/>.
        /// </summary>
        public uint Find(uint id)
        {
            while (parents.TryGetValue(id, out var next))
                id = next;

            return id;
        }

        /// <summary>
        /// Unions the classes of <paramref name="a"/> and <paramref name="b"/>, keeping the smaller representative.
        /// </summary>
        /// <returns><c>true</c> if they were distinct.</returns>
        public bool Union(uint a, uint b)
        {
            a = Find(a);
            b = Find(b);
            if (a == b)
                return false;

            parents[Math.Max(a, b)] = Math.Min(a, b);
            return true;
        }

        /// <summary>
        /// Adds a row (logical columns only).
        /// </summary>
        public void Add(ReferenceTable table, uint[] row) => rows[table].Add((uint[])row.Clone());

        /// <summary>
        /// Gets the rows of a table; canonical and grouped after <see cref="Close"/>.
        /// </summary>
        public IReadOnlyList<uint[]> Rows(ReferenceTable table) => rows[table];

        /// <summary>
        /// Looks up the output for <paramref name="key"/>, canonicalized first.
        /// </summary>
        public bool TryLookup(ReferenceTable table, ReadOnlySpan<uint> key, out uint output)
        {
            var probe = Canonical(table, key.ToArray());
            foreach (var row in rows[table])
            {
                if (row.AsSpan(0, table.KeyCount).SequenceEqual(probe.AsSpan(0, table.KeyCount)))
                {
                    output = table.HasOutput ? row[table.KeyCount] : 0;
                    return true;
                }
            }

            output = 0;
            return false;
        }

        /// <summary>
        /// Closes the view: canonicalizes every row and merges rows with the same key, unioning id outputs and merging
        /// value outputs, until a pass unions nothing.
        /// </summary>
        public void Close()
        {
            bool merged;
            do
            {
                merged = false;

                foreach (var table in rows.Keys.ToList())
                {
                    var groups = new Dictionary<string, uint[]>();

                    foreach (var original in rows[table])
                    {
                        var row = Canonical(table, original);
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
        }

        uint[] Canonical(ReferenceTable table, uint[] row)
        {
            var copy = (uint[])row.Clone();
            for (var column = 0; column < copy.Length; column++)
                if (table.IsId[column])
                    copy[column] = Find(copy[column]);

            return copy;
        }

    }

}

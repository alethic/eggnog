using System;
using System.Collections.Generic;
using System.Linq;

namespace Eggnog.Storage.Tests.Reference
{

    /// <summary>
    /// A naive e-graph: a dictionary union-find and a list of rows per table, closed by rewriting every row and
    /// grouping by key until no group merges anything.
    /// </summary>
    public sealed class ReferenceView
    {

        readonly Dictionary<ulong, ulong> parents = new();
        readonly Dictionary<Table, List<ulong[]>> rows = new();

        public ReferenceView(IReadOnlyList<Table> tables)
        {
            foreach (var table in tables)
                rows[table] = new List<ulong[]>();
        }

        public ulong Find(ulong id)
        {
            while (parents.TryGetValue(id, out var next))
                id = next;

            return id;
        }

        public bool Union(ulong a, ulong b)
        {
            a = Find(a);
            b = Find(b);
            if (a == b)
                return false;

            parents[Math.Max(a, b)] = Math.Min(a, b);
            return true;
        }

        public void Add(Table table, ulong[] row) => rows[table].Add((ulong[])row.Clone());

        public IReadOnlyList<ulong[]> Rows(Table table) => rows[table];

        public bool TryLookup(Table table, ReadOnlySpan<ulong> key, out ulong output)
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

        ulong[] Canonical(Table table, ulong[] row)
        {
            var copy = (ulong[])row.Clone();
            for (var column = 0; column < copy.Length; column++)
                if (table.IsId[column])
                    copy[column] = Find(copy[column]);

            return copy;
        }

    }

}

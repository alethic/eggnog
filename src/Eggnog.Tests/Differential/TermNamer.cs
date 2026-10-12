using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Eggnog.Tests.Differential
{

    /// <summary>
    /// Names the classes of a database by their least ground term (§8.2), so that two databases with equal facts render
    /// equal rows whatever their class ids: least by size (the number of constructor applications), then head name
    /// (ordinal), then arguments in content order, a class argument by its own name. Both sides of the differential name
    /// their classes with this one algorithm.
    /// </summary>
    internal static class TermNamer
    {

        /// <summary>
        /// Names every class that a constructor row mentions.
        /// </summary>
        /// <param name="rows">The constructor rows: the table, the keys (a class id, a <c>long</c>, for a sort column) and
        /// the output class id.</param>
        /// <returns>The names, by class id; a class with no finite term is named <c>&lt;cyclic&gt;</c>.</returns>
        public static Dictionary<long, string> Name(IReadOnlyList<(DiffTable Table, object[] Keys, long Output)> rows)
        {
            // Sizes: the least number of applications of a ground term of each class, by a fixpoint.
            var size = new Dictionary<long, long>();
            bool changed;
            do
            {
                changed = false;
                foreach (var (table, keys, output) in rows)
                {
                    var total = Size(table, keys, size);
                    if (total is { } s && (size.TryGetValue(output, out var current) == false || s < current))
                    {
                        size[output] = s;
                        changed = true;
                    }
                }
            }
            while (changed);

            // Names in increasing size: a class's children are strictly smaller, so they are named first.
            var names = new Dictionary<long, string>();
            var candidates = rows.Where(r => size.ContainsKey(r.Output) && Size(r.Table, r.Keys, size) == size[r.Output])
                .GroupBy(r => r.Output)
                .OrderBy(g => size[g.Key]);
            string NameOf(long id) => names.TryGetValue(id, out var name) ? name : "<cyclic>";
            foreach (var group in candidates)
            {
                var best = group.First();
                foreach (var row in group.Skip(1))
                    if (CompareRows(row, best, NameOf) < 0)
                        best = row;

                names[group.Key] = Render(best, NameOf);
            }

            foreach (var (table, keys, output) in rows)
            {
                names.TryAdd(output, "<cyclic>");
                for (var i = 0; i < keys.Length; i++)
                    if (table.Keys[i] == DiffType.Node)
                        names.TryAdd((long)keys[i], "<cyclic>");
            }

            return names;
        }

        /// <summary>
        /// The size of a row's term given its children's sizes; <c>null</c> while a child has none.
        /// </summary>
        static long? Size(DiffTable table, object[] keys, Dictionary<long, long> size)
        {
            long total = 1;
            for (var i = 0; i < keys.Length; i++)
            {
                if (table.Keys[i] != DiffType.Node)
                    continue;

                if (size.TryGetValue((long)keys[i], out var child) == false)
                    return null;

                total += child;
            }

            return total;
        }

        /// <summary>
        /// Orders two rows of one size: head name, then arguments in content order.
        /// </summary>
        static int CompareRows((DiffTable Table, object[] Keys, long Output) x, (DiffTable Table, object[] Keys, long Output) y, Func<long, string> names)
        {
            var order = string.CompareOrdinal(x.Table.Name, y.Table.Name);
            if (order != 0)
                return order;

            for (var i = 0; i < x.Keys.Length; i++)
            {
                order = DiffValues.Compare(x.Keys[i], y.Keys[i], x.Table.Keys[i], names);
                if (order != 0)
                    return order;
            }

            return 0;
        }

        /// <summary>
        /// Renders a row's term: <c>(C1 (C0 2) 3)</c>.
        /// </summary>
        static string Render((DiffTable Table, object[] Keys, long Output) row, Func<long, string> names)
        {
            var builder = new StringBuilder("(").Append(row.Table.Name);
            for (var i = 0; i < row.Keys.Length; i++)
                builder.Append(' ').Append(DiffValues.Format(row.Keys[i], row.Table.Keys[i], names));

            return builder.Append(')').ToString();
        }

    }

}

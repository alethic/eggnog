using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// Names the classes of a closed view by their least ground term (the algorithm of the library differential's
    /// <see cref="Eggnog.Tests.Differential.TermNamer"/>), so that two views with equal facts render equal rows
    /// whatever their ids: the engine's, minted from blocks and counters, and the reference's, minted from its own
    /// counter. Least by size (the number of constructor applications), then head name (ordinal), then arguments in
    /// order, an id argument by its own name and a value by number. Both sides of family B's comparison name their
    /// classes with this one algorithm.
    /// </summary>
    /// <remarks>
    /// Every class of a family B program is the output of a constructor term (host terms and rule mints are its only
    /// sources of ids, and constructor rows are never deleted there), so every class gets a name. A class with no
    /// finite term would be named <c>&lt;cyclic&gt;</c>, which only weakens the comparison. O(passes × constructor
    /// rows) for the sizes, then a sort.
    /// </remarks>
    static class TermNamer
    {

        /// <summary>
        /// The name of a class with no finite term.
        /// </summary>
        public const string Cyclic = "<cyclic>";

        /// <summary>
        /// Names every class that a constructor row of <paramref name="rows"/> mentions.
        /// </summary>
        /// <param name="schema">The schema.</param>
        /// <param name="rows">The canonical logical rows of each table, by table index.</param>
        /// <returns>The names, by canonical id.</returns>
        public static Dictionary<ulong, string> Name(ColorSchema schema, Func<int, IReadOnlyList<ulong[]>> rows)
        {
            ArgumentNullException.ThrowIfNull(schema);
            ArgumentNullException.ThrowIfNull(rows);
            var terms = new List<(LogicalTable Table, ulong[] Row)>();
            for (var t = 0; t < schema.Tables.Count; t++)
                if (schema.IsConstructor(t))
                    foreach (var row in rows(t))
                        terms.Add((schema.Tables[t], row));

            // Sizes: the least number of applications of a ground term of each class, by a fixpoint.
            var size = new Dictionary<ulong, long>();
            bool changed;
            do
            {
                changed = false;
                foreach (var (table, row) in terms)
                {
                    var output = row[table.KeyCount];
                    if (Size(table, row, size) is { } s && (size.TryGetValue(output, out var current) == false || s < current))
                    {
                        size[output] = s;
                        changed = true;
                    }
                }
            }
            while (changed);

            // Names in increasing size: a class's arguments are strictly smaller, so they are named first.
            var names = new Dictionary<ulong, string>();
            string NameOf(ulong id) => names.TryGetValue(id, out var name) ? name : Cyclic;
            var groups = terms.Where(t => size.ContainsKey(t.Row[t.Table.KeyCount]) && Size(t.Table, t.Row, size) == size[t.Row[t.Table.KeyCount]])
                .GroupBy(t => t.Row[t.Table.KeyCount])
                .OrderBy(g => size[g.Key]);
            foreach (var group in groups)
            {
                var best = group.First();
                foreach (var candidate in group.Skip(1))
                    if (Compare(candidate, best, NameOf) < 0)
                        best = candidate;

                names[group.Key] = Render(best, NameOf);
            }

            foreach (var (table, row) in terms)
            {
                names.TryAdd(row[table.KeyCount], Cyclic);
                for (var c = 0; c < table.KeyCount; c++)
                    if (table.IsId[c])
                        names.TryAdd(row[c], Cyclic);
            }

            return names;
        }

        /// <summary>
        /// The size of a term given its arguments' sizes; <c>null</c> while an argument has none.
        /// </summary>
        static long? Size(LogicalTable table, ulong[] row, Dictionary<ulong, long> size)
        {
            long total = 1;
            for (var c = 0; c < table.KeyCount; c++)
            {
                if (table.IsId[c] == false)
                    continue;

                if (size.TryGetValue(row[c], out var argument) == false)
                    return null;

                total += argument;
            }

            return total;
        }

        /// <summary>
        /// Orders two terms of one size: head name, then arguments in order.
        /// </summary>
        static int Compare((LogicalTable Table, ulong[] Row) x, (LogicalTable Table, ulong[] Row) y, Func<ulong, string> names)
        {
            var order = string.CompareOrdinal(x.Table.Name, y.Table.Name);
            if (order != 0)
                return order;

            for (var c = 0; c < x.Table.KeyCount; c++)
            {
                order = x.Table.IsId[c] ? string.CompareOrdinal(names(x.Row[c]), names(y.Row[c])) : x.Row[c].CompareTo(y.Row[c]);
                if (order != 0)
                    return order;
            }

            return 0;
        }

        /// <summary>
        /// Renders a term: <c>(add (num 1) (num 2))</c>.
        /// </summary>
        static string Render((LogicalTable Table, ulong[] Row) term, Func<ulong, string> names)
        {
            var builder = new StringBuilder("(").Append(term.Table.Name);
            for (var c = 0; c < term.Table.KeyCount; c++)
                builder.Append(' ').Append(term.Table.IsId[c] ? names(term.Row[c]) : term.Row[c].ToString());

            return builder.Append(')').ToString();
        }

    }

}

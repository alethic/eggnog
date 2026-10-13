using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Eggnog.Tests.Support
{

    /// <summary>
    /// A scope's facts modulo renaming of classes, the form in which runs that may mint different ids (a parallel run
    /// and a serial one) are compared: every row of every table of the graph in the scope's view, rendered with each
    /// class named by its least ground term (by size, then rendering, ordinal). Two scopes with equal facts give equal
    /// lists whatever ids their classes got. For tests.
    /// </summary>
    public static class ScopeFacts
    {

        /// <summary>
        /// Gets the scope's facts, sorted ordinal.
        /// </summary>
        /// <param name="scope">The scope.</param>
        /// <returns>One line per row: <c>(StrEq "kind" "a") = (StrEq "kind" "a")</c>,
        /// <c>(dom "kind") = {a, b}</c>.</returns>
        public static IReadOnlyList<string> Of(Scope scope)
        {
            var rows = new List<TableRow>();
            foreach (var table in scope.Graph.Tables)
                rows.AddRange(scope.Rows(table));

            var names = Name(rows);
            return rows.Select(r => Render(r, names)).OrderBy(l => l, StringComparer.Ordinal).ToArray();
        }

        /// <summary>
        /// Names the classes of constructor rows by their least ground terms.
        /// </summary>
        static Dictionary<ClassId, string> Name(List<TableRow> rows)
        {
            var terms = rows.Where(r => r.Table.Kind == TableKind.Constructor).ToArray();
            var size = new Dictionary<ClassId, long>();
            bool changed;
            do
            {
                changed = false;
                foreach (var row in terms)
                {
                    if (SizeOf(row, size) is { } s && (size.TryGetValue((ClassId)row.Output!, out var current) == false || s < current))
                    {
                        size[(ClassId)row.Output!] = s;
                        changed = true;
                    }
                }
            }
            while (changed);

            var names = new Dictionary<ClassId, string>();
            foreach (var group in terms.Where(r => size.ContainsKey((ClassId)r.Output!) && SizeOf(r, size) == size[(ClassId)r.Output!])
                         .GroupBy(r => (ClassId)r.Output!)
                         .OrderBy(g => size[g.Key]))
            {
                names[group.Key] = group.Select(r => Term(r, names)).OrderBy(n => n, StringComparer.Ordinal).First();
            }

            return names;
        }

        /// <summary>
        /// Gets the size of a row's least term, once every child class has a size.
        /// </summary>
        static long? SizeOf(TableRow row, Dictionary<ClassId, long> size)
        {
            var total = 1L;
            foreach (var key in row.Keys)
            {
                if (key is ClassId child)
                {
                    if (size.TryGetValue(child, out var s) == false)
                        return null;

                    total += s;
                }
            }

            return total;
        }

        /// <summary>
        /// Renders a constructor row as a term.
        /// </summary>
        static string Term(TableRow row, Dictionary<ClassId, string> names)
        {
            var builder = new StringBuilder("(").Append(row.Table.Name);
            foreach (var key in row.Keys)
                builder.Append(' ').Append(Cell(key, names));

            return builder.Append(')').ToString();
        }

        /// <summary>
        /// Renders a row.
        /// </summary>
        static string Render(TableRow row, Dictionary<ClassId, string> names)
        {
            var builder = new StringBuilder("(").Append(row.Table.Name);
            foreach (var key in row.Keys)
                builder.Append(' ').Append(Cell(key, names));

            builder.Append(')');
            if (row.Output is { } output)
                builder.Append(" = ").Append(Cell(output, names));

            return builder.ToString();
        }

        /// <summary>
        /// Renders a cell: a class by its name, a value by its string form.
        /// </summary>
        static string Cell(object cell, Dictionary<ClassId, string> names) => cell switch
        {
            ClassId id => names.TryGetValue(id, out var name) ? name : "<cyclic>",
            string text => "\"" + text + "\"",
            IFormattable formattable => formattable.ToString(null, System.Globalization.CultureInfo.InvariantCulture),
            _ => cell.ToString() ?? "",
        };

    }

}

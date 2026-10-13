using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// Renders the views the rule differentials compare as sorted text, one line per row, and compares two renderings:
    /// family A cell for cell (canonical ids as numbers), family B with every id replaced by its class's
    /// <see cref="TermNamer"/> name.
    /// </summary>
    static class ColorViews
    {

        /// <summary>
        /// Renders <paramref name="rows"/>, every table of the schema.
        /// </summary>
        /// <param name="schema">The schema.</param>
        /// <param name="rows">The canonical logical rows of each table, by table index.</param>
        /// <param name="byNames">Whether ids are rendered by term names (family B).</param>
        /// <returns>The rows, sorted ordinally.</returns>
        public static List<string> Render(ColorSchema schema, Func<int, IReadOnlyList<ulong[]>> rows, bool byNames)
        {
            var names = byNames ? TermNamer.Name(schema, rows) : null;
            var lines = new List<string>();
            for (var t = 0; t < schema.Tables.Count; t++)
                foreach (var row in rows(t))
                    lines.Add(Line(schema.Tables[t], row, names));

            lines.Sort(StringComparer.Ordinal);
            return lines;
        }

        /// <summary>
        /// Renders what the clean <paramref name="context"/> sees.
        /// </summary>
        public static List<string> Engine(ColorSchema schema, EGraphContext context, bool byNames)
        {
            var rows = new List<ulong[]>[schema.Tables.Count];
            for (var t = 0; t < rows.Length; t++)
                rows[t] = schema.Rows(context, t);

            return Render(schema, t => rows[t], byNames);
        }

        /// <summary>
        /// Renders a reference view.
        /// </summary>
        public static List<string> Reference(ColorSchema schema, ReferenceContextView view, bool byNames) => Render(schema, t => view.Rows(schema.Tables[t]), byNames);

        /// <summary>
        /// Renders one row: <c>table(cells)</c>, a function's output after <c>=</c>.
        /// </summary>
        public static string Line(LogicalTable table, ulong[] row, Dictionary<ulong, string>? names)
        {
            var builder = new StringBuilder(table.Name).Append('(');
            for (var c = 0; c < table.KeyCount; c++)
            {
                if (c > 0)
                    builder.Append(", ");

                builder.Append(Cell(table, row, c, names));
            }

            builder.Append(')');
            if (table.HasOutput)
                builder.Append(" = ").Append(Cell(table, row, table.KeyCount, names));

            return builder.ToString();
        }

        /// <summary>
        /// Throws with the rows each side lacks unless <paramref name="expected"/> and <paramref name="actual"/> are
        /// equal as multisets.
        /// </summary>
        /// <param name="expected">The oracle's rendering.</param>
        /// <param name="actual">The engine's rendering.</param>
        /// <param name="what">What is compared, for the message.</param>
        public static void AssertSame(List<string> expected, List<string> actual, string what)
        {
            if (expected.SequenceEqual(actual))
                return;

            var missing = Subtract(expected, actual);
            var extra = Subtract(actual, expected);
            var message = new StringBuilder($"{what}: the views differ ({expected.Count} expected rows, {actual.Count} actual).");
            message.AppendLine();
            message.AppendLine($"  missing ({missing.Count}): {string.Join("; ", missing.Take(24))}");
            message.Append($"  unexpected ({extra.Count}): {string.Join("; ", extra.Take(24))}");
            throw new Xunit.Sdk.XunitException(message.ToString());
        }

        /// <summary>
        /// Renders a cell: an id by name or number, a value by number.
        /// </summary>
        static string Cell(LogicalTable table, ulong[] row, int column, Dictionary<ulong, string>? names)
        {
            var cell = row[column];
            if (table.IsId[column] && names is not null)
                return names.TryGetValue(cell, out var name) ? name : TermNamer.Cyclic;

            return cell.ToString();
        }

        /// <summary>
        /// Gets the lines of <paramref name="a"/> not matched by a line of <paramref name="b"/>, multiplicities
        /// counted.
        /// </summary>
        static List<string> Subtract(List<string> a, List<string> b)
        {
            var counts = new Dictionary<string, int>();
            foreach (var line in b)
                counts[line] = counts.GetValueOrDefault(line) + 1;

            var result = new List<string>();
            foreach (var line in a)
            {
                if (counts.TryGetValue(line, out var n) && n > 0)
                    counts[line] = n - 1;
                else
                    result.Add(line);
            }

            return result;
        }

    }

}

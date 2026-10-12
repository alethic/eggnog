using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Eggnog.Tests.Differential
{

    /// <summary>
    /// The values of a random program, as both sides of the differential see them: <c>long</c>, <c>string</c>,
    /// <c>ValueSet&lt;int&gt;</c>, and for the sort a class id (a <c>long</c>) that a <see cref="TermNamer"/> name
    /// replaces. Formatting and content order are the same on both sides.
    /// </summary>
    internal static class DiffValues
    {

        /// <summary>
        /// Formats a value: <c>42</c>, <c>"a"</c>, <c>{1, 3}</c>, or a class's name.
        /// </summary>
        /// <param name="value">The value; for the sort, a class id.</param>
        /// <param name="type">Its type.</param>
        /// <param name="names">Names a class.</param>
        /// <returns>The text.</returns>
        public static string Format(object value, DiffType type, Func<long, string> names) => type switch
        {
            DiffType.I64 => ((long)value).ToString(CultureInfo.InvariantCulture),
            DiffType.Str => "\"" + (string)value + "\"",
            DiffType.IntSet => "{" + string.Join(", ", (ValueSet<int>)value) + "}",
            DiffType.Node => names((long)value),
            DiffType.Bool => (bool)value ? "true" : "false",
            _ => throw new ArgumentOutOfRangeException(nameof(type), type, null),
        };

        /// <summary>
        /// Compares two values of one type in content order: numbers numerically, strings ordinally, sets
        /// lexicographically, classes by name.
        /// </summary>
        /// <param name="x">One value.</param>
        /// <param name="y">The other value.</param>
        /// <param name="type">Their type.</param>
        /// <param name="names">Names a class.</param>
        /// <returns>The order.</returns>
        public static int Compare(object x, object y, DiffType type, Func<long, string> names)
        {
            switch (type)
            {
                case DiffType.I64:
                    return ((long)x).CompareTo((long)y);
                case DiffType.Str:
                    return string.CompareOrdinal((string)x, (string)y);
                case DiffType.IntSet:
                {
                    var a = (ValueSet<int>)x;
                    var b = (ValueSet<int>)y;
                    for (var i = 0; i < a.Count && i < b.Count; i++)
                        if (a[i] != b[i])
                            return a[i].CompareTo(b[i]);

                    return a.Count.CompareTo(b.Count);
                }
                case DiffType.Node:
                    return string.CompareOrdinal(names((long)x), names((long)y));
                default:
                    return ((bool)x).CompareTo((bool)y);
            }
        }

        /// <summary>
        /// Renders a row: <c>(r0 1 "a")</c>, <c>(f0 "b") = 3</c>, <c>(C0 2) = (C0 2)</c>.
        /// </summary>
        /// <param name="table">The table.</param>
        /// <param name="keys">The keys.</param>
        /// <param name="output">The output; ignored for a relation.</param>
        /// <param name="names">Names a class.</param>
        /// <returns>The text.</returns>
        public static string Row(DiffTable table, IReadOnlyList<object> keys, object? output, Func<long, string> names)
        {
            var builder = new StringBuilder("(").Append(table.Name);
            for (var i = 0; i < table.Keys.Length; i++)
                builder.Append(' ').Append(Format(keys[i], table.Keys[i], names));

            builder.Append(')');
            if (table.Kind != TableKind.Relation)
                builder.Append(" = ").Append(Format(output!, table.Output!.Value, names));

            return builder.ToString();
        }

    }

}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

using Eggnog.Tests.Support;

namespace Eggnog.Tests.Values
{

    /// <summary>
    /// A scope's facts, written without ids, so that a child's view and a fresh root's replay of the same commands can
    /// be compared as text (<see cref="Of"/>).
    /// </summary>
    /// <remarks>
    /// Each class is named by its shortest constructor term over named classes (ties broken by ordinal order), found by
    /// rounds over the constructors' rows; a container is written as its named elements, a set's and a multiset's and a
    /// map's in name order. Each table's rows are written in that form, sorted; then the number of named classes, and the
    /// cost of extracting each. A row naming a class no term names is written with <c>?</c>.
    /// </remarks>
    static class ContainerProgramView
    {

        /// <summary>
        /// Gets the view of <paramref name="scope"/>.
        /// </summary>
        /// <param name="theory">The graph.</param>
        /// <param name="scope">The scope.</param>
        /// <returns>The view.</returns>
        public static string Of(ContainerProgramTheory theory, Scope scope)
        {
            var rows = new Dictionary<TableHandle, IReadOnlyList<TableRow>>();
            foreach (var table in theory.Tables)
                rows[table] = scope.Rows(table);

            var names = new Dictionary<ClassId, string>();
            var nameOf = Namer(theory, scope, rows, names);
            var text = new StringBuilder();
            foreach (var table in theory.Tables)
            {
                var lines = new SortedSet<string>(StringComparer.Ordinal);
                foreach (var row in rows[table])
                {
                    var keys = string.Join(",", row.Keys.Select(k => nameOf(k) ?? "?"));
                    var output = table is RelationHandle ? "" : "=" + (nameOf(row.Output) ?? "?");
                    lines.Add(keys + output + (row.Subsumed ? "!" : ""));
                }

                text.Append(table.Name).Append(':').AppendJoin(";", lines).Append('\n');
            }

            var classes = names.OrderBy(entry => entry.Value, StringComparer.Ordinal).ToList();
            text.Append("classes:").Append(classes.Count).Append('\n');
            foreach (var (id, name) in classes)
                text.Append(name).Append('$').Append(scope.Extract(new Node(id)).Cost).Append(' ');

            return text.Append('\n').ToString();
        }

        /// <summary>
        /// Gets the names of <paramref name="scope"/>'s classes and values, as <see cref="Of"/> writes them: a function
        /// from a value to its name, <c>null</c> for one that holds a class no term names.
        /// </summary>
        /// <param name="theory">The graph.</param>
        /// <param name="scope">The scope.</param>
        /// <returns>The function.</returns>
        public static Func<object?, string?> Names(ContainerProgramTheory theory, Scope scope)
        {
            var rows = new Dictionary<TableHandle, IReadOnlyList<TableRow>>();
            foreach (var table in theory.Constructors)
                rows[table] = scope.Rows(table);

            return Namer(theory, scope, rows, new Dictionary<ClassId, string>());
        }

        /// <summary>
        /// Names the classes of <paramref name="scope"/> into <paramref name="names"/> from the constructors' rows in
        /// <paramref name="rows"/>, and gets the function that names a value.
        /// </summary>
        static Func<object?, string?> Namer(ContainerProgramTheory theory, Scope scope, Dictionary<TableHandle, IReadOnlyList<TableRow>> rows,
            Dictionary<ClassId, string> names)
        {
            ClassId Canonical(ClassId id) => scope.Find(new Node(id)).Id;
            string? Name(object? value)
            {
                switch (value)
                {
                    case ClassId id:
                        return names.TryGetValue(Canonical(id), out var name) ? name : null;
                    case Node node:
                        return names.TryGetValue(Canonical(node.Id), out var nodeName) ? nodeName : null;
                    case long or bool or string:
                        return value.ToString();
                    case Vec<Vec<Node>> nested:
                        return Join("[", nested.Select(v => Name(v)), "]", false);
                    case Vec<Node> vector:
                        return Join("[", vector.Select(x => Name(x)), "]", false);
                    case Set<Node> set:
                        return Join("{", set.Select(x => Name(x)), "}", true);
                    case MultiSet<Node> multiset:
                        return Join("ms{", multiset.Select(x => Name(x)), "}", true);
                    case Map<Node, Node> map:
                        return Join("map{", map.Select(entry => Name(entry.Key) is { } key && Name(entry.Value) is { } mapped ? key + ":" + mapped : null), "}", true);
                    case Pair<Node, Node> pair:
                        return Join("<", new[] { Name(pair.First), Name(pair.Second) }, ">", false);
                    case UnstableFn<Node, Node> function:
                        return Join(function.Name + "(", function.Arguments.Select(Name), ")", false);
                    default:
                        return "?" + value?.GetType().Name;
                }
            }

            for (var round = 0; round < 10; round++)
            {
                var changed = false;
                foreach (var table in theory.Constructors)
                {
                    foreach (var row in rows[table])
                    {
                        var keys = row.Keys.Select(Name).ToList();
                        if (keys.Any(k => k is null))
                            continue;

                        var candidate = table.Name + "(" + string.Join(",", keys) + ")";
                        var output = Canonical((ClassId)row.Output!);
                        if (names.TryGetValue(output, out var current) == false || Shorter(candidate, current))
                        {
                            names[output] = candidate;
                            changed = true;
                        }
                    }
                }

                if (changed == false)
                    break;
            }

            return Name;
        }

        /// <summary>
        /// Joins named parts, sorted when <paramref name="sort"/>; <c>null</c> when a part has no name.
        /// </summary>
        static string? Join(string open, IEnumerable<string?> parts, string close, bool sort)
        {
            var list = parts.ToList();
            if (list.Any(p => p is null))
                return null;

            var ordered = sort ? list.Order(StringComparer.Ordinal) : (IEnumerable<string?>)list;
            return open + string.Join(",", ordered) + close;
        }

        /// <summary>
        /// Gets whether <paramref name="candidate"/> is a better name than <paramref name="current"/>: shorter, or as
        /// long and first in ordinal order.
        /// </summary>
        static bool Shorter(string candidate, string current) =>
            candidate.Length < current.Length || (candidate.Length == current.Length && string.CompareOrdinal(candidate, current) < 0);

    }

}

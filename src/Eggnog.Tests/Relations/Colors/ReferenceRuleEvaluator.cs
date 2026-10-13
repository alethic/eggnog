using System.Collections.Generic;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// The naive rule evaluation of <see cref="ReferenceRuleContext"/>: enumerates a <see cref="ColorRule"/>'s matches
    /// over a closed <see cref="ReferenceContextView"/> by nested loops in atom order, keeps those its filters pass
    /// and, for a semi-naive run, those that use a row the rule has not seen, and runs the rule's actions on each,
    /// collecting what they derive.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The view is canonical, so bindings are canonical ids. Id constants run in their canonical form in the view, as a
    /// store-backed context runs them; value constants run as written. A read (<see cref="ColorActionKind.Read"/>)
    /// sees the view, as the engine's actions see the committed rows, not the iteration's writes; so does a mint, which
    /// reuses the view's term or the iteration's earlier mint of it.
    /// </para>
    /// <para>
    /// Semi-naive selection is by content: <c>seen</c> holds the rows (<see cref="RowKey"/>) present, with the same
    /// canonical content, at the rule's watermark and ever since (<see cref="ReferenceRuleContext"/>); a match fires
    /// when one of its rows is not among them, which is the engine's "some atom's row is stamped at or after the
    /// watermark", since the engine stamps a row exactly when its canonical content changes. <c>null</c> fires every
    /// match.
    /// </para>
    /// </remarks>
    static class ReferenceRuleEvaluator
    {

        /// <summary>
        /// Evaluates <paramref name="rule"/> over <paramref name="view"/> into <paramref name="output"/>.
        /// </summary>
        /// <param name="schema">The schema.</param>
        /// <param name="rule">The rule, its id constants resolved.</param>
        /// <param name="view">The closed view the run reads.</param>
        /// <param name="seen">The rows the rule has seen, or <c>null</c> for a full run.</param>
        /// <param name="output">Receives what the fired matches derive.</param>
        /// <returns>The number of matches that fired.</returns>
        public static int Evaluate(ColorSchema schema, ColorRule rule, ReferenceContextView view, HashSet<string>? seen, ReferenceRuleDerivations output)
        {
            var atoms = new List<(ColorAtom Atom, List<ulong[]> Rows, string[] Keys)>();
            foreach (var atom in rule.Atoms)
            {
                var table = schema.Tables[atom.Table];
                var rows = new List<ulong[]>();
                var keys = new List<string>();
                foreach (var row in view.Rows(table))
                {
                    var ok = true;
                    for (var c = 0; c < atom.Entries.Length && ok; c++)
                        if (atom.Entries[c] < 0)
                            ok = row[c] == Cell(view, atom.Entries[c], table.IsId[c]);

                    if (ok)
                    {
                        rows.Add(row);
                        keys.Add(RowKey(table, row));
                    }
                }

                atoms.Add((atom, rows, keys.ToArray()));
            }

            var binding = new ulong[rule.VarIsId.Count];
            var bound = new bool[rule.VarIsId.Count];
            var fired = 0;
            Join(0, false);
            return fired;

            void Join(int depth, bool anyNew)
            {
                if (depth == atoms.Count)
                {
                    if (seen is not null && anyNew == false)
                        return;

                    foreach (var filter in rule.Filters)
                    {
                        var a = binding[filter.A];
                        var b = binding[filter.B];
                        if (filter.Kind == ColorFilterKind.Ne ? a == b : a >= b)
                            return;
                    }

                    fired++;
                    Apply(schema, rule, view, binding, output);
                    return;
                }

                var (atom, rows, keys) = atoms[depth];
                var newly = new List<int>();
                for (var r = 0; r < rows.Count; r++)
                {
                    var row = rows[r];
                    var ok = true;
                    for (var c = 0; c < atom.Entries.Length && ok; c++)
                    {
                        var e = atom.Entries[c];
                        if (e < 0)
                            continue;

                        if (bound[e])
                        {
                            ok = binding[e] == row[c];
                        }
                        else
                        {
                            bound[e] = true;
                            binding[e] = row[c];
                            newly.Add((int)e);
                        }
                    }

                    if (ok)
                        Join(depth + 1, anyNew || seen is null || seen.Contains(keys[r]) == false);

                    foreach (var v in newly)
                        bound[v] = false;

                    newly.Clear();
                }
            }
        }

        /// <summary>
        /// Gets the content key of a canonical logical row: the table's name and the cells.
        /// </summary>
        public static string RowKey(LogicalTable table, ulong[] row) => table.Name + ":" + string.Join(",", row);

        /// <summary>
        /// Runs the actions of one fired match.
        /// </summary>
        static void Apply(ColorSchema schema, ColorRule rule, ReferenceContextView view, ulong[] binding, ReferenceRuleDerivations output)
        {
            ulong Value(long e, bool isId) => e >= 0 ? binding[e] : Cell(view, e, isId);

            foreach (var action in rule.Actions)
            {
                switch (action.Kind)
                {
                    case ColorActionKind.Set:
                    {
                        var table = schema.Tables[action.Table];
                        var row = new ulong[action.Args.Length];
                        for (var c = 0; c < row.Length; c++)
                            row[c] = Value(action.Args[c], table.IsId[c]);

                        output.Rows.Add((table, row));
                        break;
                    }
                    case ColorActionKind.Union:
                        output.Unions.Add((Value(action.Args[0], true), Value(action.Args[1], true)));
                        break;
                    case ColorActionKind.Mint:
                    {
                        var table = schema.Tables[action.Table];
                        var key = new ulong[action.Args.Length];
                        for (var c = 0; c < key.Length; c++)
                            key[c] = Value(action.Args[c], table.IsId[c]);

                        binding[action.Result] = view.TryLookup(table, key, out var id) ? id : output.Mint(table, key);
                        break;
                    }
                    default:
                    {
                        var key = new[] { Value(action.Args[0], true) };
                        binding[action.Result] = view.TryLookup(schema.Tables[ColorSchema.Low], key, out var value) ? value : ColorSchema.LowDefault;
                        break;
                    }
                }
            }
        }

        /// <summary>
        /// Gets the cell a constant entry runs as: an id constant's canonical form in the view, or the value.
        /// </summary>
        static ulong Cell(ReferenceContextView view, long entry, bool isId)
        {
            var value = ColorRule.Decode(entry);
            return isId ? view.Find(value) : value;
        }

    }

}

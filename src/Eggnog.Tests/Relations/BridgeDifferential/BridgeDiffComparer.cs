using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// Compares two <see cref="BridgeDiffView"/>s of one program modulo a renaming of ids: the views agree when some
    /// bijection between their classes maps the classes of the seeds onto each other and every function's rows onto each
    /// other, cell for cell (integers and the unit value as they are), subsumption flags included.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The bijection is not searched for; it is derived. The ids the seeds minted before the rules ran are numbered alike
    /// in every evaluation, and later seeds name their classes, so those classes correspond by construction. Every other
    /// class was minted by a rule as the output of a constructor or relation row whose keys were existing classes, so the
    /// rest of the bijection follows by propagation: a row of the expected view whose keys are mapped must have a
    /// counterpart in the actual view with the mapped keys, and the two outputs correspond. The propagation is a worklist
    /// (a row is ready once its id keys are mapped), linear in the rows. A class neither seeded nor derived this way, a
    /// correspondence that is not one-to-one, or a row without its counterpart is a disagreement.
    /// </para>
    /// <para>
    /// Each view is first checked on its own: every id cell canonical, every key held by one row. That catches an engine
    /// that leaves rows behind after a rebuild, which a renaming could otherwise hide.
    /// </para>
    /// </remarks>
    static class BridgeDiffComparer
    {

        /// <summary>
        /// Throws <see cref="BridgeDiffMismatchException"/> unless <paramref name="actual"/> agrees with
        /// <paramref name="expected"/> modulo renaming.
        /// </summary>
        /// <param name="program">The program both views come from.</param>
        /// <param name="expected">The reference view.</param>
        /// <param name="actual">The view under test.</param>
        /// <param name="expectedName">The reference's name, for the message.</param>
        /// <param name="actualName">The name of the evaluation under test.</param>
        /// <param name="where">When the views were taken, for the message.</param>
        /// <exception cref="BridgeDiffMismatchException">The views disagree.</exception>
        public static void Compare(BridgeDiffProgram program, BridgeDiffView expected, BridgeDiffView actual, string expectedName, string actualName, string where)
        {
            var functions = program.Functions;
            var forward = new Dictionary<uint, uint>();
            var backward = new Dictionary<uint, uint>();

            void Fail(string message)
            {
                var b = new StringBuilder();
                b.Append(where).Append(": ").Append(actualName).Append(" disagrees with ").Append(expectedName).Append(": ").AppendLine(message);
                b.Append("  ").Append(expectedName).AppendLine(":");
                Describe(b, program, expected);
                b.Append("  ").Append(actualName).AppendLine(":");
                Describe(b, program, actual);
                throw new BridgeDiffMismatchException(b.ToString());
            }

            CheckWellFormed(program, expected, expectedName, Fail);
            var actualIndex = CheckWellFormed(program, actual, actualName, Fail);

            // The propagation's worklist (set up once the seeds' pairs are bound): the expected rows whose output is an id,
            // how many of each row's id keys are not mapped yet, the rows waiting for each class, and the rows ready.
            var pending = new List<(int Function, BridgeDiffRow Row)>();
            var missing = new List<int>();
            var waiting = new Dictionary<uint, List<int>>();
            var ready = new Queue<int>();

            // Returns whether the pair is new; fails, with the reason why() gives, when it contradicts an earlier pair. A
            // new pair readies the rows waiting for the class.
            bool Bind(uint e, uint a, Func<string> why)
            {
                var hasForward = forward.TryGetValue(e, out var mappedA);
                if (hasForward && mappedA == a)
                    return false;

                if (hasForward)
                    Fail($"{why()}: {expectedName}'s class {e} corresponds to {actualName}'s classes {mappedA} and {a}.");

                if (backward.TryGetValue(a, out var mappedE))
                    Fail($"{why()}: {actualName}'s class {a} corresponds to {expectedName}'s classes {mappedE} and {e}.");

                forward.Add(e, a);
                backward.Add(a, e);
                if (waiting.Remove(e, out var rows))
                {
                    foreach (var r in rows)
                    {
                        if (--CollectionsMarshal.AsSpan(missing)[r] == 0)
                            ready.Enqueue(r);
                    }
                }

                return true;
            }

            if (expected.InitialIds != actual.InitialIds)
                Fail($"the seeds minted {expected.InitialIds} ids in {expectedName} and {actual.InitialIds} in {actualName}.");

            for (var id = 0u; id < (uint)expected.InitialIds; id++)
                Bind(expected.Find(id), actual.Find(id), () => $"id {id}, minted by a seed");

            for (var i = 0; i < program.Seeds.Length; i++)
            {
                var (e, a) = (expected.SeedId(i), actual.SeedId(i));
                if ((e == uint.MaxValue) != (a == uint.MaxValue))
                    Fail($"seed {i} produced an id in one evaluation only.");

                var seed = i;
                if (e != uint.MaxValue)
                    Bind(expected.Find(e), actual.Find(a), () => $"the class of $s{seed}");
            }

            // Propagate the correspondence through every row whose output is an id: a row is ready once its id keys are
            // mapped, and binding its output to its counterpart's may ready more. Linear in the rows.
            var mapped = new uint[functions.Length == 0 ? 1 : functions.Max(f => f.Columns)];
            bool TryMap(BridgeDiffFunction function, BridgeDiffRow row, int columns)
            {
                for (var c = 0; c < columns; c++)
                {
                    var cell = row.Values[c];
                    if (function.IsIdColumn(c))
                    {
                        if (forward.TryGetValue(cell, out var image) == false)
                            return false;

                        cell = image;
                    }

                    mapped[c] = cell;
                }

                return true;
            }

            string Mapped(int count) => string.Join(", ", mapped.Take(count));

            for (var f = 0; f < functions.Length; f++)
            {
                var function = functions[f];
                if (function.OutputIsId == false)
                    continue;

                foreach (var row in expected.Rows(f))
                {
                    var r = pending.Count;
                    var count = 0;
                    for (var c = 0; c < function.KeyCount; c++)
                    {
                        var cell = row.Values[c];
                        if (function.IsIdColumn(c) == false || forward.ContainsKey(cell))
                            continue;

                        // A class repeated in the key waits once per occurrence and is counted once per occurrence.
                        if (waiting.TryGetValue(cell, out var list) == false)
                            waiting.Add(cell, list = new List<int>());

                        list.Add(r);
                        count++;
                    }

                    pending.Add((f, row));
                    missing.Add(count);
                    if (count == 0)
                        ready.Enqueue(r);
                }
            }

            while (ready.TryDequeue(out var next))
            {
                var (f, row) = pending[next];
                var function = functions[f];
                if (TryMap(function, row, function.KeyCount) == false)
                    throw new InvalidOperationException($"The comparison readied {row.Format(function)} before its keys were mapped.");

                if (actualIndex[f].TryGetValue(new BridgeDiffKey(mapped, function.KeyCount), out var counterpart) == false)
                    Fail($"{expectedName} holds {row.Format(function)}, but {actualName} has no row for the key ({Mapped(function.KeyCount)}).");

                Bind(row.Values[function.KeyCount], counterpart.Values[function.KeyCount], () => $"the outputs of {row.Format(function)} and {counterpart.Format(function)}");
            }

            CheckCovered(expected, forward, expectedName, actualName, Fail);
            CheckCovered(actual, backward, actualName, expectedName, Fail);

            // Every row must now have its image, and the counts must agree, which makes the images a bijection.
            for (var f = 0; f < functions.Length; f++)
            {
                var function = functions[f];
                var rows = expected.Rows(f);
                if (rows.Count != actual.Rows(f).Count)
                    Fail($"{function.Name} holds {rows.Count} rows in {expectedName} and {actual.Rows(f).Count} in {actualName}.");

                foreach (var row in rows)
                {
                    if (TryMap(function, row, function.Columns) == false)
                        Fail($"{expectedName} holds {row.Format(function)}, whose classes have no counterparts.");

                    if (actualIndex[f].TryGetValue(new BridgeDiffKey(mapped, function.KeyCount), out var counterpart) == false)
                        Fail($"{expectedName} holds {row.Format(function)}, but {actualName} has no row for the key ({Mapped(function.KeyCount)}).");

                    if (counterpart.Values[function.KeyCount] != mapped[function.KeyCount] || counterpart.Subsumed != row.Subsumed)
                        Fail($"{expectedName} holds {row.Format(function)}, which maps to ({Mapped(function.Columns)}){(row.Subsumed ? " subsumed" : "")}, but {actualName} holds {counterpart.Format(function)}.");
                }
            }
        }

        /// <summary>
        /// Fails unless every id cell of <paramref name="view"/> is a canonical id it knows and every key is held by one
        /// row, and returns each function's rows indexed by their keys.
        /// </summary>
        static Dictionary<BridgeDiffKey, BridgeDiffRow>[] CheckWellFormed(BridgeDiffProgram program, BridgeDiffView view, string name, Action<string> fail)
        {
            var indexes = new Dictionary<BridgeDiffKey, BridgeDiffRow>[program.Functions.Length];
            for (var f = 0; f < program.Functions.Length; f++)
            {
                var function = program.Functions[f];
                var keys = indexes[f] = new Dictionary<BridgeDiffKey, BridgeDiffRow>(view.Rows(f).Count);
                foreach (var row in view.Rows(f))
                {
                    if (row.Values.Length != function.Columns)
                        fail($"{name} holds a row of {function.Name} with {row.Values.Length} cells.");

                    for (var c = 0; c < function.Columns; c++)
                    {
                        var cell = row.Values[c];
                        if (function.IsIdColumn(c) && (cell >= (uint)view.IdCount || view.Find(cell) != cell))
                            fail($"{name} holds {row.Format(function)}, whose column {c} is not a canonical id (its class is {view.Find(cell)}).");
                    }

                    if (row.Subsumed && function.CanSubsume == false)
                        fail($"{name} holds {row.Format(function)}, subsumed, but {function.Name} cannot subsume.");

                    if (keys.TryAdd(new BridgeDiffKey(row.Values, function.KeyCount), row) == false)
                        fail($"{name} holds two rows of {function.Name} for the key of {row.Format(function)}.");
                }
            }

            return indexes;
        }

        /// <summary>
        /// Fails unless every class of <paramref name="view"/> has a counterpart in <paramref name="map"/>.
        /// </summary>
        static void CheckCovered(BridgeDiffView view, Dictionary<uint, uint> map, string name, string otherName, Action<string> fail)
        {
            for (var id = 0u; id < (uint)view.IdCount; id++)
            {
                var root = view.Find(id);
                if (map.ContainsKey(root) == false)
                    fail($"{name}'s class {root} (of id {id}) corresponds to no class of {otherName}: no seed names it and no row derives it from one that does.");
            }
        }

        /// <summary>
        /// Appends a listing of <paramref name="view"/>: its classes with more than one id, then every function's rows,
        /// sorted, at most 40 per function.
        /// </summary>
        static void Describe(StringBuilder b, BridgeDiffProgram program, BridgeDiffView view)
        {
            var classes = Enumerable.Range(0, view.IdCount).GroupBy(id => view.Find((uint)id)).Where(g => g.Count() > 1).Select(g => "{" + string.Join(" ", g) + "}").ToArray();
            b.Append("    ").Append(view.IdCount).Append(" ids; classes ").AppendLine(classes.Length == 0 ? "(all singletons)" : string.Join(" ", classes));
            for (var f = 0; f < program.Functions.Length; f++)
            {
                var function = program.Functions[f];
                var rows = view.Rows(f).Select(r => r.Format(function)).OrderBy(s => s, StringComparer.Ordinal).ToArray();
                b.Append("    ").Append(function.Name).Append(": ");
                b.AppendJoin("; ", rows.Take(40));
                if (rows.Length > 40)
                    b.Append("; ... (").Append(rows.Length).Append(" rows)");

                b.AppendLine();
            }
        }

    }

}

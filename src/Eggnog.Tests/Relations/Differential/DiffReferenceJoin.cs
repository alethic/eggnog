using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// The oracle of the differential suite: a naive nested-loop join of a query's atoms over the model's rows. Every
    /// combination of rows, one per atom, whose cells match the atom's constants, agree on repeated and shared
    /// variables and satisfy the atom's constraints is a match; the result is the set of distinct matches, keyed by the
    /// values of the bound variables, which <see cref="Project"/> reduces to the recorded variables of a projected query.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The loops visit the atoms in a greedy order (most variables already bound first, then fewest candidate rows),
    /// each atom's rows are prefiltered by its own constants and constraints, an atom without variables keeps one
    /// candidate row (every row it keeps makes the same matches), and each atom's candidates are hashed on the columns
    /// whose variables the atoms before it bind, so a loop visits only the rows that agree with the binding so far (and
    /// still checks every cell); none of that changes the set of matches, only how long it takes. A work budget bounds
    /// the number of row visits and the number of matches; a query beyond it is reported as too expensive and the
    /// caller drops it.
    /// </para>
    /// <para>
    /// A projected query is joined in full first and projected afterwards, so the budget bounds its full matches too:
    /// the engine enumerates them all, whatever the action reads, so a projection onto few variables of a product of
    /// large components would otherwise reach the engine (soak seed 9100124 has a cross product of four components
    /// read through one variable, which the engine takes more than half an hour to enumerate).
    /// </para>
    /// </remarks>
    static class DiffReferenceJoin
    {

        /// <summary>
        /// Joins <paramref name="atoms"/> over the rows of <paramref name="model"/>.
        /// </summary>
        /// <param name="model">The model.</param>
        /// <param name="atoms">The atoms, with every constraint that applies (extra and header constraints included).</param>
        /// <param name="bound">The bound variables, ascending: the columns of a match.</param>
        /// <param name="slotCount">The size of the binding arrays returned (query variables and lookup results).</param>
        /// <param name="budget">The maximum number of row visits.</param>
        /// <param name="maxMatches">The maximum number of distinct matches.</param>
        /// <returns>The distinct matches, keyed by <see cref="Key(ReadOnlySpan{uint})"/> of the bound values, each with a
        /// binding array (unbound slots zero); or <c>null</c> when the budget ran out.</returns>
        public static Dictionary<string, uint[]>? Join(DiffModel model, IReadOnlyList<DiffAtom> atoms, int[] bound, int slotCount, long budget, int maxMatches)
        {
            var n = atoms.Count;
            var candidates = new List<uint[]>[n];
            for (var i = 0; i < n; i++)
            {
                candidates[i] = Prefilter(atoms[i], model.Rows(atoms[i].Table));
                if (candidates[i].Count > 1 && atoms[i].Entries.All(e => e.IsVariable == false || e.IsWildcard))
                    candidates[i].RemoveRange(1, candidates[i].Count - 1);
            }

            var order = Order(atoms, candidates);
            var (probeVariables, indexes) = Index(atoms, order, candidates);
            var binding = new uint[slotCount];
            var isBound = new bool[slotCount];
            var result = new Dictionary<string, uint[]>(StringComparer.Ordinal);
            var values = new uint[bound.Length];
            var work = 0L;
            return Recurse(0) ? result : null;

            bool Recurse(int depth)
            {
                if (depth == n)
                {
                    for (var i = 0; i < bound.Length; i++)
                        values[i] = binding[bound[i]];

                    var key = Key(values);
                    if (result.ContainsKey(key) == false)
                    {
                        result.Add(key, (uint[])binding.Clone());
                        if (result.Count > maxMatches)
                            return false;
                    }

                    return true;
                }

                var atom = atoms[order[depth]];
                var rows = candidates[order[depth]];
                if (indexes[depth] is { } index)
                {
                    var probe = probeVariables[depth];
                    Span<uint> key = stackalloc uint[probe.Length];
                    for (var i = 0; i < probe.Length; i++)
                        key[i] = binding[probe[i]];

                    if (++work > budget)
                        return false;
                    if (index.TryGetValue(Key(key), out var matching) == false)
                        return true;

                    rows = matching;
                }

                Span<int> added = stackalloc int[atom.Entries.Length];
                foreach (var row in rows)
                {
                    if (++work > budget)
                        return false;

                    var count = 0;
                    var ok = true;
                    for (var c = 0; c < row.Length && ok; c++)
                    {
                        var e = atom.Entries[c];
                        if (e.IsVariable == false || e.IsWildcard)
                            continue;

                        var v = e.Variable.Index;
                        if (isBound[v])
                        {
                            ok = binding[v] == row[c];
                        }
                        else
                        {
                            binding[v] = row[c];
                            isBound[v] = true;
                            added[count++] = v;
                        }
                    }

                    if (ok && Recurse(depth + 1) == false)
                        return false;

                    for (var i = 0; i < count; i++)
                        isBound[added[i]] = false;
                }

                return true;
            }
        }

        /// <summary>
        /// Hashes the candidates of the atom at each depth of <paramref name="order"/> on its columns whose variables the
        /// atoms before it bind (each variable once, at its first column; repeats were prefiltered).
        /// </summary>
        /// <returns>Per depth, the variables of the hashed columns and the rows by their values (<see cref="Key"/>), or
        /// <c>null</c> when no column is bound before.</returns>
        static (int[][] Variables, Dictionary<string, List<uint[]>>?[] Indexes) Index(IReadOnlyList<DiffAtom> atoms, int[] order, List<uint[]>[] candidates)
        {
            var variables = new int[order.Length][];
            var indexes = new Dictionary<string, List<uint[]>>?[order.Length];
            var seen = new HashSet<int>();
            for (var depth = 0; depth < order.Length; depth++)
            {
                var atom = atoms[order[depth]];
                var columns = new List<int>();
                var vars = new List<int>();
                for (var c = 0; c < atom.Entries.Length; c++)
                {
                    var e = atom.Entries[c];
                    if (e.IsVariable && e.IsWildcard == false && seen.Contains(e.Variable.Index) && vars.Contains(e.Variable.Index) == false)
                    {
                        columns.Add(c);
                        vars.Add(e.Variable.Index);
                    }
                }

                variables[depth] = vars.ToArray();
                if (columns.Count > 0)
                {
                    var index = new Dictionary<string, List<uint[]>>(StringComparer.Ordinal);
                    var key = new uint[columns.Count];
                    foreach (var row in candidates[order[depth]])
                    {
                        for (var i = 0; i < key.Length; i++)
                            key[i] = row[columns[i]];

                        var k = Key(key);
                        if (index.TryGetValue(k, out var list) == false)
                            index.Add(k, list = new List<uint[]>());

                        list.Add(row);
                    }

                    indexes[depth] = index;
                }

                foreach (var e in atom.Entries)
                    if (e.IsVariable && e.IsWildcard == false)
                        seen.Add(e.Variable.Index);
            }

            return (variables, indexes);
        }

        /// <summary>
        /// Projects the matches of <see cref="Join"/> onto <paramref name="recorded"/>: the distinct values of those
        /// variables, each with the binding array of one of the matches that has them.
        /// </summary>
        /// <param name="matches">The matches, with their binding arrays.</param>
        /// <param name="recorded">The recorded variables, ascending.</param>
        /// <returns>The projected matches, keyed by <see cref="Key(ReadOnlySpan{uint})"/> of the recorded values.</returns>
        public static Dictionary<string, uint[]> Project(Dictionary<string, uint[]> matches, int[] recorded)
        {
            var result = new Dictionary<string, uint[]>(StringComparer.Ordinal);
            var values = new uint[recorded.Length];
            foreach (var binding in matches.Values)
            {
                for (var i = 0; i < recorded.Length; i++)
                    values[i] = binding[recorded[i]];

                result.TryAdd(Key(values), binding);
            }

            return result;
        }

        /// <summary>
        /// Builds the key of a match from the values of its variables, in variable order.
        /// </summary>
        public static string Key(ReadOnlySpan<uint> values)
        {
            var b = new StringBuilder(values.Length * 3);
            for (var i = 0; i < values.Length; i++)
            {
                if (i > 0)
                    b.Append(',');

                b.Append(values[i]);
            }

            return b.ToString();
        }

        /// <summary>
        /// Keeps the rows that satisfy the atom's constants, its constraints and its repeated variables.
        /// </summary>
        static List<uint[]> Prefilter(DiffAtom atom, List<uint[]> rows)
        {
            var result = new List<uint[]>();
            foreach (var row in rows)
            {
                if (Constraint.EvaluateAll(atom.Constraints, row) == false)
                    continue;

                var ok = true;
                for (var c = 0; c < row.Length && ok; c++)
                {
                    var e = atom.Entries[c];
                    if (e.IsVariable == false)
                    {
                        ok = row[c] == e.Constant.Rep;
                    }
                    else if (e.IsWildcard == false)
                    {
                        // A repeated variable must agree with its first column.
                        for (var p = 0; p < c && ok; p++)
                            if (atom.Entries[p].IsVariable && atom.Entries[p].IsWildcard == false && atom.Entries[p].Variable == e.Variable)
                                ok = row[p] == row[c];
                    }
                }

                if (ok)
                    result.Add(row);
            }

            return result;
        }

        /// <summary>
        /// Orders the atoms greedily: next the atom with the most variables bound by the atoms before it, then the one with
        /// the fewest candidate rows.
        /// </summary>
        static int[] Order(IReadOnlyList<DiffAtom> atoms, List<uint[]>[] candidates)
        {
            var n = atoms.Count;
            var order = new int[n];
            var used = new bool[n];
            var seen = new HashSet<int>();
            for (var k = 0; k < n; k++)
            {
                var best = -1;
                var bestShared = -1;
                for (var i = 0; i < n; i++)
                {
                    if (used[i])
                        continue;

                    var shared = atoms[i].Entries.Where(e => e.IsVariable && e.IsWildcard == false).Select(e => e.Variable.Index).Distinct().Count(seen.Contains);
                    if (best < 0 || shared > bestShared || (shared == bestShared && candidates[i].Count < candidates[best].Count))
                    {
                        best = i;
                        bestShared = shared;
                    }
                }

                used[best] = true;
                order[k] = best;
                foreach (var e in atoms[best].Entries)
                    if (e.IsVariable && e.IsWildcard == false)
                        seen.Add(e.Variable.Index);
            }

            return order;
        }

    }

}

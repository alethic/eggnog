using System;
using System.Collections.Generic;
using System.Linq;

namespace Eggnog.Relations.Tests
{

    sealed partial class BridgeDiffOracle
    {

        // The state of the iteration being evaluated.
        List<(uint[] Cells, bool Subsumed)>[] snapshot = [];
        Dictionary<(int Function, int Column, uint Value), List<int>> columnIndex = new();
        readonly Dictionary<(int Function, BridgeDiffKey Key), uint> predicted = new();
        readonly List<(int Function, uint[] Cells, bool Subsumed)> writes = new();
        readonly List<(uint A, uint B)> unions = new();
        uint[] fireBinding = [];
        bool[] fireBound = [];
        int matches;

        /// <summary>
        /// Gets the number of matches the last <see cref="Step"/> enumerated (before primitive filters).
        /// </summary>
        public int LastMatches => matches;

        /// <summary>
        /// Gets the number of ids the last <see cref="Step"/> minted.
        /// </summary>
        public int LastMinted { get; private set; }

        /// <summary>
        /// Runs one naive iteration of the given rules: every rule is matched against the closed e-graph as it is now,
        /// every action of every match is applied, and the e-graph is closed again.
        /// </summary>
        /// <param name="ruleIndexes">The rules to run, by index in the program.</param>
        /// <returns>Whether the iteration changed anything, or <c>null</c> when it exceeded the program's
        /// <see cref="BridgeDiffProgram.MatchBudget"/>, which leaves the oracle unusable.</returns>
        /// <exception cref="BridgeDiffPanicException">A match panicked, as the bridge would; the oracle is unusable
        /// afterwards.</exception>
        public bool? Step(int[] ruleIndexes)
        {
            snapshot = new List<(uint[], bool)>[tables.Length];
            for (var f = 0; f < tables.Length; f++)
                snapshot[f] = tables[f].Select(e => (Cells(e.Key, e.Value.Output), e.Value.Subsumed)).ToList();

            columnIndex = new();
            predicted.Clear();
            writes.Clear();
            unions.Clear();
            matches = 0;
            var before = IdCount;

            foreach (var r in ruleIndexes)
            {
                var rule = program.Rules[r];
                var binding = new uint[rule.VarSorts.Length];
                var bound = new bool[rule.VarSorts.Length];
                fireBinding = new uint[rule.VarSorts.Length];
                fireBound = new bool[rule.VarSorts.Length];
                if (Match(rule, 0, binding, bound) == false)
                    return null;
            }

            LastMinted = IdCount - before;

            // The writes land together after every rule ran, then the unions, then congruence closure.
            var changed = false;
            foreach (var (function, cells, subsumed) in writes)
                changed |= Write(function, cells, subsumed);

            foreach (var (a, b) in unions)
                changed |= Union(a, b);

            Close();
            return changed;
        }

        /// <summary>
        /// Enumerates the matches of atoms <paramref name="atom"/> onwards by nested loops (using a column index for the
        /// first column a constant or bound variable fixes), firing each complete match. A relation atom without an output
        /// (<see cref="BridgeDiffFunction.IsNoOutput"/>) matches any output, as the frontend's fresh variable does.
        /// </summary>
        /// <returns><c>false</c> when the match budget ran out.</returns>
        bool Match(BridgeDiffRule rule, int atom, uint[] binding, bool[] bound)
        {
            if (atom == rule.Atoms.Length)
            {
                if (++matches > program.MatchBudget)
                    return false;

                // Fire on a copy: the primitives and actions bind variables the next match must find unbound.
                binding.CopyTo(fireBinding, 0);
                bound.CopyTo(fireBound, 0);
                Fire(rule, fireBinding, fireBound);
                return true;
            }

            var (f, entries, subsumed) = rule.Atoms[atom];
            var function = program.Functions[f];
            var columns = function.IsNoOutput(entries[function.KeyCount]) ? function.KeyCount : entries.Length;
            var rows = snapshot[f];
            IEnumerable<int> candidates = Enumerable.Range(0, rows.Count);
            for (var c = 0; c < columns; c++)
            {
                var e = entries[c];
                if (e.IsVar == false || bound[e.Var])
                {
                    candidates = Candidates(f, c, e.IsVar ? binding[e.Var] : RuleConstant(e));
                    break;
                }
            }

            var canSubsume = function.CanSubsume;
            var newlyBound = new List<int>(entries.Length);
            foreach (var r in candidates)
            {
                var (cells, isSubsumed) = rows[r];

                // The subsumption entry: false matches rows that are not subsumed, true subsumed ones, null any.
                if (canSubsume && subsumed is bool wanted && wanted != isSubsumed)
                    continue;

                var ok = true;
                for (var c = 0; c < columns && ok; c++)
                {
                    var e = entries[c];
                    if (e.IsVar == false)
                    {
                        ok = cells[c] == RuleConstant(e);
                    }
                    else if (bound[e.Var])
                    {
                        ok = binding[e.Var] == cells[c];
                    }
                    else
                    {
                        bound[e.Var] = true;
                        binding[e.Var] = cells[c];
                        newlyBound.Add(e.Var);
                    }
                }

                if (ok && Match(rule, atom + 1, binding, bound) == false)
                    return false;

                foreach (var v in newlyBound)
                    bound[v] = false;

                newlyBound.Clear();
            }

            return true;
        }

        /// <summary>
        /// Gets the cell a rule's constant runs as: an eq-sort constant in its canonical form in the snapshot, as a
        /// store-backed e-graph runs every Id constant of a rule (colored-contexts spec X19, §19 item 4: a constant that
        /// lost a union keeps matching its class, where egglog's cached plan stops matching); any other constant as
        /// written.
        /// </summary>
        uint RuleConstant(BridgeDiffEntry entry) => BridgeDiffSort.IsEq(entry.Sort) ? Find(entry.Value) : Constant(entry);

        /// <summary>
        /// Gets the rows of function <paramref name="f"/> whose column <paramref name="column"/> holds
        /// <paramref name="value"/>, indexing the column on first use in the iteration.
        /// </summary>
        IEnumerable<int> Candidates(int f, int column, uint value)
        {
            if (columnIndex.ContainsKey((f, column, uint.MaxValue)) == false)
            {
                // Build the column's index, marking it built with a sentinel entry no cell can collide with
                // (uint.MaxValue is the engine's stale marker, never a value).
                var rows = snapshot[f];
                for (var r = 0; r < rows.Count; r++)
                {
                    var key = (f, column, rows[r].Cells[column]);
                    if (columnIndex.TryGetValue(key, out var list) == false)
                        columnIndex[key] = list = new List<int>();

                    list.Add(r);
                }

                columnIndex[(f, column, uint.MaxValue)] = new List<int>();
            }

            return columnIndex.TryGetValue((f, column, value), out var matching) ? matching : Array.Empty<int>();
        }

        /// <summary>
        /// Runs a complete match: the primitives filter it (or bind their results), then the actions run in order, reading
        /// the snapshot and collecting writes and unions.
        /// </summary>
        /// <exception cref="BridgeDiffPanicException">An action panicked.</exception>
        void Fire(BridgeDiffRule rule, uint[] binding, bool[] bound)
        {
            uint Value(BridgeDiffEntry e) => e.IsVar ? binding[e.Var] : RuleConstant(e);

            foreach (var prim in rule.Prims)
            {
                if (BridgeDiffPrimitives.TryEvaluate(prim.Primitive, Value(prim.Left), Value(prim.Right), unit, out var result) == false)
                    return;

                var r = prim.Result;
                if (r.IsVar && bound[r.Var] == false)
                {
                    bound[r.Var] = true;
                    binding[r.Var] = result;
                }
                else if (Value(r) != result)
                {
                    return;
                }
            }

            foreach (var action in rule.Actions)
            {
                // A fresh array per action: writes and the lookups' predictions keep it.
                var cells = new uint[action.Entries.Length];
                for (var i = 0; i < cells.Length; i++)
                    cells[i] = Value(action.Entries[i]);

                switch (action.Kind)
                {
                    case BridgeDiffActionKind.Lookup:
                        binding[action.Result] = LookupOrInsert(rule, action.Function, cells, false);
                        bound[action.Result] = true;
                        break;
                    case BridgeDiffActionKind.Set when program.Functions[action.Function].IsNoOutput(action.Entries[^1]):
                        // egglog's (R a b) action: a lookup that inserts the key.
                        LookupOrInsert(rule, action.Function, cells, false);
                        break;
                    case BridgeDiffActionKind.Set:
                        writes.Add((action.Function, cells, false));
                        break;
                    case BridgeDiffActionKind.Union:
                        unions.Add((cells[0], cells[1]));
                        break;
                    case BridgeDiffActionKind.Subsume:
                        LookupOrInsert(rule, action.Function, cells, true);
                        break;
                    default:
                    {
                        if (BridgeDiffPrimitives.TryEvaluate(action.Primitive, cells[0], cells[1], unit, out var result) == false)
                            throw new BridgeDiffPanicException($"{BridgeDiffPrimitives.Name(action.Primitive)} produced nothing on the right-hand side of {rule.Name}.");

                        binding[action.Result] = result;
                        bound[action.Result] = true;
                        break;
                    }
                }
            }
        }

        /// <summary>
        /// Builds a row from the first <paramref name="keyCount"/> cells of <paramref name="cells"/> and an output.
        /// </summary>
        static uint[] Row(uint[] cells, int keyCount, uint output)
        {
            var row = new uint[keyCount + 1];
            Array.Copy(cells, row, keyCount);
            row[keyCount] = output;
            return row;
        }

        /// <summary>
        /// Looks a key of a function up: the snapshot's output, else the output an earlier lookup of the iteration chose,
        /// else a fresh id (constructor or relation), recorded and written. A subsumption writes the row subsumed whatever
        /// it found.
        /// </summary>
        /// <returns>The output.</returns>
        /// <exception cref="BridgeDiffPanicException">The function has no default and no row for the key.</exception>
        uint LookupOrInsert(BridgeDiffRule rule, int f, uint[] cells, bool subsume)
        {
            var function = program.Functions[f];
            var key = new BridgeDiffKey(cells, function.KeyCount);
            uint output;
            if (tables[f].TryGetValue(key, out var row))
            {
                output = row.Output;
            }
            else if (predicted.TryGetValue((f, key), out output) == false)
            {
                if (function.HasDefault == false)
                    throw new BridgeDiffPanicException($"{rule.Name} read {function.Name}{key}, which it does not hold and has no default for.");

                output = Mint();
                predicted[(f, key)] = output;
                writes.Add((f, Row(cells, function.KeyCount, output), subsume));
                return output;
            }

            if (subsume)
                writes.Add((f, Row(cells, function.KeyCount, output), true));

            return output;
        }

    }

}

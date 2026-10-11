using System;
using System.Collections.Generic;
using System.Linq;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// A slow reference evaluator of plan templates over a <see cref="PlanTestDatabase"/>, following the executor's
    /// semantics (core-relations/src/free_join/execute.rs) with set semantics for materializations. It checks that
    /// every variable a stage reads was bound by an earlier stage, and can reorder stages with
    /// <see cref="StageOrdering"/> at the start of each block and at every depth, as the executor may.
    /// </summary>
    sealed class PlanInterpreter
    {

        readonly PlanTestDatabase db;
        readonly PlanTemplate plan;
        readonly uint[] bindings;
        readonly bool[] bound;
        readonly List<int>[] rows;
        readonly List<Dictionary<string, (uint[] Key, HashSet<string> Seen, List<uint[]> Values)>> mats = new();
        readonly long[] timesRefined;
        readonly bool reorder;

        public PlanInterpreter(PlanTestDatabase db, PlanTemplate plan, bool reorder)
        {
            this.db = db;
            this.plan = plan;
            this.reorder = reorder;
            bindings = new uint[plan.VariableCount];
            bound = new bool[plan.VariableCount];
            rows = new List<int>[plan.Atoms.IdCount];
            timesRefined = new long[plan.Atoms.IdCount];
        }

        /// <summary>
        /// Runs the plan and returns the distinct tuples of <paramref name="output"/> values at the leaves.
        /// </summary>
        public HashSet<string> Run(Variable[] output)
        {
            var results = new HashSet<string>();
            ResetRows();
            foreach (var check in plan.ExistenceChecks)
            {
                var t = db[plan.Atoms[check.Atom].Table];
                if (rows[check.Atom.Index].Any(r => t.Matches(r, check.SlowConstraints)) == false)
                    return results;
            }

            switch (plan)
            {
                case SinglePlan single:
                    RunStages(single.Stages, () => results.Add(Project(output)));
                    break;
                case DecomposedPlan decomposed:
                    for (var i = 0; i < decomposed.Blocks.Length; i++)
                    {
                        var block = decomposed.Blocks[i];
                        var mat = new Dictionary<string, (uint[] Key, HashSet<string> Seen, List<uint[]> Values)>();
                        mats.Add(mat);
                        ResetRows();
                        RunStages(block.Stages, () => Materialize(mat, block.Spec));

                        // The executor skips the run when a materialization is empty.
                        if (mat.Count == 0)
                            return results;
                    }

                    ResetRows();
                    RunStages(decomposed.ResultBlock, () => results.Add(Project(output)));
                    break;
            }

            return results;
        }

        void ResetRows()
        {
            foreach (var (atom, info) in plan.Atoms)
            {
                var t = db[info.Table];
                rows[atom.Index] = Enumerable.Range(0, t.Rows.Count).Where(r => t.Matches(r, info.FastConstraints)).ToList();
            }
        }

        string Project(Variable[] output)
        {
            foreach (var v in output)
                if (bound[v.Index] == false)
                    throw new InvalidOperationException($"Output variable {v} is not bound at the leaf.");

            return string.Join(",", output.Select(v => bindings[v.Index]));
        }

        void Materialize(Dictionary<string, (uint[] Key, HashSet<string> Seen, List<uint[]> Values)> mat, MatSpec spec)
        {
            var key = spec.MessageVariables.Select(Read).ToArray();
            var values = spec.ValueVariables.Select(Read).ToArray();
            var k = string.Join(",", key);
            if (mat.TryGetValue(k, out var entry) == false)
            {
                entry = (key, new HashSet<string>(), new List<uint[]>());
                mat.Add(k, entry);
            }

            if (entry.Seen.Add(string.Join(",", values)))
                entry.Values.Add(values);
        }

        uint Read(Variable v)
        {
            if (bound[v.Index] == false)
                throw new InvalidOperationException($"Variable {v} read before it is bound.");

            return bindings[v.Index];
        }

        void Bind(Variable v, uint value)
        {
            bindings[v.Index] = value;
            bound[v.Index] = true;
        }

        void RunStages(JoinStage[] stages, Action leaf)
        {
            var order = new InstrOrder(stages.Length);
            var leafScans = new bool[stages.Length];
            Array.Clear(bound);
            if (reorder)
            {
                var sizes = new InterpreterSizes(this);
                StageOrdering.SortPlanBySize(order, leafScans, 0, stages, ref sizes, timesRefined);
            }

            Recurse(stages, order, leafScans, 0, leaf);
        }

        void Recurse(JoinStage[] stages, InstrOrder order, bool[] leafScans, int depth, Action leaf)
        {
            if (depth == stages.Length)
            {
                leaf();
                return;
            }

            if (reorder && depth > 0 && depth < stages.Length - 1)
            {
                var sizes = new InterpreterSizes(this);
                StageOrdering.SortPlanBySize(order, leafScans, depth, stages, ref sizes, timesRefined);
            }

            var stage = stages[order[depth]];
            switch (stage)
            {
                case IntersectStage s:
                    RunIntersect(stages, order, leafScans, depth, leaf, s);
                    break;
                case FusedIntersectStage f:
                    RunFused(stages, order, leafScans, depth, leaf, f);
                    break;
                case FusedIntersectMatStage m:
                    RunMat(stages, order, leafScans, depth, leaf, m);
                    break;
            }
        }

        List<int> Filter(AtomId atom, Constraint[] constraints)
        {
            var t = db[plan.Atoms[atom].Table];
            return rows[atom.Index].Where(r => t.Matches(r, constraints)).ToList();
        }

        uint Cell(AtomId atom, int row, ColumnId column) => db[plan.Atoms[atom].Table].Rows[row][column.Index];

        void RunIntersect(JoinStage[] stages, InstrOrder order, bool[] leafScans, int depth, Action leaf, IntersectStage s)
        {
            var saved = s.Scans.Select(sc => rows[sc.Atom.Index]).ToArray();
            var filtered = s.Scans.Select(sc => Filter(sc.Atom, sc.Constraints)).ToArray();
            HashSet<uint>? values = null;
            for (var i = 0; i < s.Scans.Length; i++)
            {
                var sc = s.Scans[i];
                var vs = filtered[i].Select(r => Cell(sc.Atom, r, sc.Column)).ToHashSet();
                if (values is null)
                    values = vs;
                else
                    values.IntersectWith(vs);
            }

            foreach (var value in values!.OrderBy(v => v))
            {
                Bind(s.Variable, value);
                for (var i = 0; i < s.Scans.Length; i++)
                {
                    var sc = s.Scans[i];
                    rows[sc.Atom.Index] = filtered[i].Where(r => Cell(sc.Atom, r, sc.Column) == value).ToList();
                }

                Recurse(stages, order, leafScans, depth + 1, leaf);
            }

            for (var i = 0; i < s.Scans.Length; i++)
                rows[s.Scans[i].Atom.Index] = saved[i];

            bound[s.Variable.Index] = false;
        }

        void RunFused(JoinStage[] stages, InstrOrder order, bool[] leafScans, int depth, Action leaf, FusedIntersectStage f)
        {
            var coverAtom = f.Cover.Atom;
            var savedCover = rows[coverAtom.Index];
            var savedProbes = f.ToIntersect.Select(p => rows[p.Scan.Atom.Index]).ToArray();
            var cover = Filter(coverAtom, f.Cover.Constraints);
            var probeRows = f.ToIntersect.Select(p => Filter(p.Scan.Atom, p.Scan.Constraints)).ToArray();
            foreach (var r in cover)
            {
                var tuple = f.Cover.ToIndex.Columns.Select(c => Cell(coverAtom, r, c)).ToArray();
                foreach (var b in f.Bind)
                    Bind(b.Variable, Cell(coverAtom, r, b.Column));

                rows[coverAtom.Index] = [r];
                var ok = true;
                for (var k = 0; k < f.ToIntersect.Length && ok; k++)
                {
                    var p = f.ToIntersect[k];
                    var cols = p.Scan.ToIndex.Columns;
                    var matching = probeRows[k].Where(pr => Enumerable.Range(0, cols.Length).All(c => Cell(p.Scan.Atom, pr, cols[c]) == tuple[p.Key[c].Index])).ToList();
                    rows[p.Scan.Atom.Index] = matching;
                    ok = matching.Count > 0;
                }

                if (ok)
                    Recurse(stages, order, leafScans, depth + 1, leaf);

                for (var k = 0; k < f.ToIntersect.Length; k++)
                    rows[f.ToIntersect[k].Scan.Atom.Index] = savedProbes[k];
            }

            rows[coverAtom.Index] = savedCover;
            foreach (var b in f.Bind)
                bound[b.Variable.Index] = false;
        }

        void RunMat(JoinStage[] stages, InstrOrder order, bool[] leafScans, int depth, Action leaf, FusedIntersectMatStage m)
        {
            var mat = mats[m.Cover.Index];
            switch (m.Mode)
            {
                case MatScanMode.KeyOnly:
                    {
                        var savedProbes = m.ToIntersect.Select(p => rows[p.Scan.Atom.Index]).ToArray();
                        foreach (var (key, _, _) in mat.Values)
                        {
                            foreach (var b in m.Bind)
                                Bind(b.Variable, key[b.Column.Index]);

                            var ok = true;
                            for (var k = 0; k < m.ToIntersect.Length && ok; k++)
                            {
                                var p = m.ToIntersect[k];
                                var cols = p.Scan.ToIndex.Columns;
                                var matching = savedProbes[k].Where(pr => Enumerable.Range(0, cols.Length).All(c => Cell(p.Scan.Atom, pr, cols[c]) == key[p.Key[c].Index])).ToList();
                                rows[p.Scan.Atom.Index] = matching;
                                ok = matching.Count > 0;
                            }

                            if (ok)
                                Recurse(stages, order, leafScans, depth + 1, leaf);

                            for (var k = 0; k < m.ToIntersect.Length; k++)
                                rows[m.ToIntersect[k].Scan.Atom.Index] = savedProbes[k];
                        }

                        break;
                    }
                case MatScanMode.Full:
                    foreach (var (key, _, values) in mat.Values)
                    {
                        foreach (var val in values)
                        {
                            foreach (var b in m.Bind)
                                Bind(b.Variable, b.Column.Index < key.Length ? key[b.Column.Index] : val[b.Column.Index - key.Length]);

                            Recurse(stages, order, leafScans, depth + 1, leaf);
                        }
                    }

                    break;
                case MatScanMode.Value:
                    {
                        var k = string.Join(",", m.ModeVariables.Select(Read));
                        if (mat.TryGetValue(k, out var entry))
                        {
                            foreach (var val in entry.Values)
                            {
                                foreach (var b in m.Bind)
                                    Bind(b.Variable, val[b.Column.Index]);

                                Recurse(stages, order, leafScans, depth + 1, leaf);
                            }
                        }

                        break;
                    }
                case MatScanMode.Lookup:
                    if (mat.ContainsKey(string.Join(",", m.ModeVariables.Select(Read))))
                        Recurse(stages, order, leafScans, depth + 1, leaf);
                    break;
            }

            foreach (var b in m.Bind)
                bound[b.Variable.Index] = false;
        }

        internal int AtomSize(AtomId atom) => rows[atom.Index].Count;

        internal int MatSize(MatId mat) => mats[mat.Index].Count;

    }

}

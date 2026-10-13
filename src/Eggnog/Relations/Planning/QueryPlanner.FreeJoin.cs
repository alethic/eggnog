using System;
using System.Collections.Generic;

namespace Eggnog.Relations
{

    internal static partial class QueryPlanner
    {

        /// <summary>
        /// Plans a bag with Free Join. egglog's <c>plan_free_join</c> (core-relations/src/free_join/plan.rs:1424-1463):
        /// the candidate covers are every atom (<see cref="PlanStrategy.PureSize"/>) or the greedy set cover
        /// (<see cref="PlanStrategy.MinCover"/>), in atom order, stably sorted by approximate size; each yields a stage
        /// binding its variables not yet bound.
        /// </summary>
        /// <param name="ctx">The bag.</param>
        /// <param name="state">The planning state.</param>
        /// <param name="strategy"><see cref="PlanStrategy.PureSize"/> or <see cref="PlanStrategy.MinCover"/>.</param>
        /// <param name="stages">Receives the stages.</param>
        static void PlanFreeJoin(PlanningContext ctx, PlanningState state, PlanStrategy strategy, List<JoinStage> stages)
        {
            var candidates = new List<(AtomId Atom, long Size, int Order)>();
            if (strategy == PlanStrategy.PureSize)
            {
                foreach (var (atom, _) in ctx.Atoms)
                    candidates.Add((atom, ctx.GetApproximateSize(atom), candidates.Count));
            }
            else
            {
                var eligible = new HashSet<AtomId>();
                var queue = new BucketQueue(ctx);
                while (queue.TryPop(out var atom))
                    eligible.Add(atom);

                foreach (var (atom, _) in ctx.Atoms)
                    if (eligible.Contains(atom))
                        candidates.Add((atom, ctx.GetApproximateSize(atom), candidates.Count));
            }

            // Rust's sort_by_key is stable: equal sizes keep atom order.
            candidates.Sort((a, b) => a.Size != b.Size ? a.Size.CompareTo(b.Size) : a.Order.CompareTo(b.Order));

            var next = 0;
            while (GetNextFreeJoinStage(ctx, state, candidates, ref next) is { } info)
                stages.Add(CompileStage(ctx, state, info));
        }

        /// <summary>
        /// Takes the next candidate cover that still has unbound variables and describes its stage. egglog's
        /// <c>get_next_freejoin_stage</c> (core-relations/src/free_join/plan.rs:1465-1525): the cover binds every new
        /// variable of the atom, in column order (a repeated variable once, from its first column), and every other atom
        /// holding one of them is probed with a key of positions in the cover's tuple.
        /// </summary>
        /// <remarks>
        /// <para>
        /// A bag's atoms may hold variables outside the bag; egglog looks them up in the bag's variable map, which
        /// panics, and sizes its bitsets too small for them (plan.rs:1481-1491). Those columns are not expanded in the
        /// bag, so they are skipped here, as generic join skips them.
        /// </para>
        /// <para>
        /// Deterministic probe order: egglog drains a hash map, so probes come in FxHash order; here they are sorted by
        /// atom id.
        /// </para>
        /// </remarks>
        /// <param name="ctx">The bag.</param>
        /// <param name="state">The planning state; updated.</param>
        /// <param name="ordering">The candidate covers in order.</param>
        /// <param name="next">The position of the next candidate; advanced.</param>
        /// <returns>The stage, or <c>null</c> once the candidates are exhausted.</returns>
        static StageInfo? GetNextFreeJoinStage(PlanningContext ctx, PlanningState state, List<(AtomId Atom, long Size, int Order)> ordering, ref int next)
        {
            while (next < ordering.Count)
            {
                var atom = ordering[next++].Atom;
                var info = ctx.Atoms[atom];
                var coverColumns = new List<ColumnId>();
                var vars = new List<Variable>();
                var probeAtoms = new List<AtomId>();
                var probeColumns = new List<List<ColumnId>>();
                foreach (var (column, variable) in info.VarColumns)
                {
                    if (ctx.Variables.TryGetValue(variable, out var vinfo) == false)
                        continue;
                    if (state.IsVariableUsed(variable))
                        continue;

                    state.MarkVariableUsed(variable);
                    vars.Add(variable);
                    coverColumns.Add(column);
                    foreach (var sub in vinfo.Occurrences)
                    {
                        if (sub.Atom == atom)
                            continue;

                        var k = probeAtoms.IndexOf(sub.Atom);
                        if (k < 0)
                        {
                            probeAtoms.Add(sub.Atom);
                            probeColumns.Add(new List<ColumnId>());
                            k = probeAtoms.Count - 1;
                        }

                        probeColumns[k].AddRange(sub.Columns);
                    }
                }

                // Fully bound by earlier stages: try the next candidate.
                if (vars.Count == 0)
                    continue;

                var order = new int[probeAtoms.Count];
                for (var k = 0; k < order.Length; k++)
                    order[k] = k;
                Array.Sort(order, (x, y) => probeAtoms[x].Index.CompareTo(probeAtoms[y].Index));

                var filters = new List<(SubAtom SubAtom, ColumnId[] Key)>(order.Length);
                foreach (var k in order)
                {
                    var probed = ctx.Atoms[probeAtoms[k]];
                    var columns = probeColumns[k];
                    var key = new ColumnId[columns.Count];
                    for (var c = 0; c < key.Length; c++)
                        key[c] = new ColumnId(vars.IndexOf(probed.GetVariable(columns[c])));

                    filters.Add((new SubAtom(probeAtoms[k], columns.ToArray()), key));
                }

                return new StageInfo(new SubAtom(atom, coverColumns.ToArray()), vars, filters);
            }

            return null;
        }

    }

}

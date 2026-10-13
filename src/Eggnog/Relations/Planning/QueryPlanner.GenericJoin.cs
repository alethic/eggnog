using System.Collections.Generic;

namespace Eggnog.Relations
{

    internal static partial class QueryPlanner
    {

        /// <summary>
        /// Plans a bag with Generic Join, one variable per stage. egglog's <c>plan_gj</c>
        /// (core-relations/src/free_join/plan.rs:1527-1581).
        /// </summary>
        /// <remarks>
        /// Variables are planned in ascending id order (the executor reorders by size at run time). A variable with one
        /// occurrence that the action does not read is skipped unless its atom would otherwise go unvisited; that is how
        /// per-atom timestamp variables avoid enumeration. A variable's cover is its first occurrence; the rest are
        /// filters. Then <see cref="FuseSingleScans"/>.
        /// </remarks>
        /// <param name="ctx">The bag.</param>
        /// <param name="state">The planning state.</param>
        /// <param name="stages">Receives the stages.</param>
        static void PlanGenericJoin(PlanningContext ctx, PlanningState state, List<JoinStage> stages)
        {
            var planned = new List<Variable>();
            var atomsHit = new FixedBitSet(ctx.Atoms.IdCount);
            foreach (var (variable, info) in ctx.Variables)
            {
                var n = info.Occurrences.Count;

                // No occurrences: bound on the right-hand side, or unused.
                if (n == 0)
                    continue;

                // Plan it below only if its atom is otherwise unmentioned.
                if (n == 1 && info.UsedInRhs == false)
                    continue;

                foreach (var sub in info.Occurrences)
                    atomsHit.Insert(sub.Atom.Index);

                planned.Add(variable);
            }

            foreach (var (variable, info) in ctx.Variables)
            {
                if (info.Occurrences.Count != 1 || info.UsedInRhs)
                    continue;

                // Unused, but the join must still visit every relation.
                var sub = info.Occurrences[0];
                if (atomsHit.Insert(sub.Atom.Index))
                    planned.Add(variable);
            }

            foreach (var variable in planned)
            {
                var occurrences = ctx.Variables[variable].Occurrences;
                var filters = new List<(SubAtom SubAtom, ColumnId[] Key)>(occurrences.Count - 1);
                for (var i = 1; i < occurrences.Count; i++)
                    filters.Add((occurrences[i], new ColumnId[occurrences[i].Columns.Length]));

                stages.Add(CompileStage(ctx, state, new StageInfo(occurrences[0], [variable], filters)));
            }

            FuseSingleScans(stages);
        }

        /// <summary>
        /// Merges every probe-free <see cref="FusedIntersectStage"/> into the first earlier probe-free one on the same
        /// cover atom, so each atom contributes at most one single-scan stage (which the executor can run as a factorized
        /// leaf scan). egglog's <c>fuse_single_scans</c> (core-relations/src/free_join/plan.rs:159-209): covers,
        /// constraints and bindings are concatenated.
        /// </summary>
        /// <param name="stages">The stages; rewritten in place.</param>
        static void FuseSingleScans(List<JoinStage> stages)
        {
            var i = 0;
            while (i < stages.Count)
            {
                if (stages[i] is not FusedIntersectStage { ToIntersect.Length: 0 } current)
                {
                    i++;
                    continue;
                }

                var target = -1;
                for (var j = 0; j < i; j++)
                {
                    if (stages[j] is FusedIntersectStage { ToIntersect.Length: 0 } earlier && earlier.Cover.Atom == current.Cover.Atom)
                    {
                        target = j;
                        break;
                    }
                }

                if (target < 0)
                {
                    i++;
                    continue;
                }

                // Don't advance i: the removal shifts later stages down by one.
                stages.RemoveAt(i);
                var into = (FusedIntersectStage)stages[target];
                var cover = new ScanSpec(into.Cover.ToIndex.Append(current.Cover.ToIndex.Columns), [.. into.Cover.Constraints, .. current.Cover.Constraints]);
                stages[target] = new FusedIntersectStage(cover, [.. into.Bind, .. current.Bind], []);
            }
        }

    }

}

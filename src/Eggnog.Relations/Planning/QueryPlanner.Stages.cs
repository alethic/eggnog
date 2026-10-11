using System;
using System.Collections.Generic;

namespace Eggnog.Relations
{

    internal static partial class QueryPlanner
    {

        /// <summary>
        /// Builds the header specs of a bag: the fast constraints of each atom that has any, in atom order. egglog's
        /// <c>plan_headers</c> (core-relations/src/free_join/plan.rs:1368-1404), minus the subsets.
        /// </summary>
        /// <param name="ctx">The bag.</param>
        /// <returns>The header specs.</returns>
        static JoinHeaderSpec[] PlanHeaders(PlanningContext ctx)
        {
            var headers = new List<JoinHeaderSpec>();
            foreach (var (atom, info) in ctx.Atoms)
                if (info.FastConstraints.Length > 0)
                    headers.Add(new JoinHeaderSpec(atom, (Constraint[])info.FastConstraints.Clone()));

            return headers.ToArray();
        }

        /// <summary>
        /// Plans the stages of one bag with <paramref name="strategy"/>. egglog's <c>plan_stages</c>
        /// (core-relations/src/free_join/plan.rs:1406-1422).
        /// </summary>
        /// <param name="ctx">The bag.</param>
        /// <param name="strategy">The strategy.</param>
        /// <returns>The header specs and the stages, which the caller may still extend.</returns>
        static (JoinHeaderSpec[] Headers, List<JoinStage> Stages) PlanStages(PlanningContext ctx, PlanStrategy strategy)
        {
            var headers = PlanHeaders(ctx);
            var stages = new List<JoinStage>();
            var state = new PlanningState(ctx.VariableCapacity, ctx.Atoms.IdCount);
            switch (strategy)
            {
                case PlanStrategy.PureSize:
                case PlanStrategy.MinCover:
                    PlanFreeJoin(ctx, state, strategy, stages);
                    break;
                case PlanStrategy.Gj:
                    PlanGenericJoin(ctx, state, stages);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(strategy));
            }

            return (headers, stages);
        }

        /// <summary>
        /// Lowers a <see cref="StageInfo"/> to a stage, attaching each atom's slow constraints to its first scan in plan
        /// order. egglog's <c>compile_stage</c> (core-relations/src/free_join/plan.rs:1583-1655): one variable with
        /// filters becomes an <see cref="IntersectStage"/> (cover first, then the filters, each on its first column);
        /// anything else a <see cref="FusedIntersectStage"/> binding each variable from its (last) column in the cover.
        /// </summary>
        /// <param name="ctx">The bag.</param>
        /// <param name="state">The planning state; updated.</param>
        /// <param name="info">The stage description.</param>
        /// <returns>The stage.</returns>
        static JoinStage CompileStage(PlanningContext ctx, PlanningState state, StageInfo info)
        {
            if (info.Variables.Count == 1 && info.Filters.Count > 0)
            {
                var scans = new SingleScanSpec[info.Filters.Count + 1];
                scans[0] = SingleScan(ctx, state, info.Cover);
                for (var i = 0; i < info.Filters.Count; i++)
                    scans[i + 1] = SingleScan(ctx, state, info.Filters[i].SubAtom);

                return new IntersectStage(info.Variables[0], scans);
            }

            var coverAtom = info.Cover.Atom;
            var coverInfo = ctx.Atoms[coverAtom];
            var cover = new ScanSpec(info.Cover, state.TakeAtomConstraintsIfNew(coverAtom, coverInfo));
            var bind = new StageBinding[info.Variables.Count];
            for (var i = 0; i < bind.Length; i++)
                bind[i] = new StageBinding(coverInfo.GetColumn(info.Variables[i]), info.Variables[i]);

            var probes = new Probe[info.Filters.Count];
            for (var i = 0; i < probes.Length; i++)
            {
                var (sub, key) = info.Filters[i];
                probes[i] = new Probe(new ScanSpec(sub, state.TakeAtomConstraintsIfNew(sub.Atom, ctx.Atoms[sub.Atom])), key);
            }

            return new FusedIntersectStage(cover, bind, probes);
        }

        static SingleScanSpec SingleScan(PlanningContext ctx, PlanningState state, SubAtom sub)
        {
            return new SingleScanSpec(sub.Atom, sub.Columns[0], state.TakeAtomConstraintsIfNew(sub.Atom, ctx.Atoms[sub.Atom]));
        }

    }

}

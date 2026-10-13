using System;
using System.Collections.Generic;

namespace Eggnog.Relations
{

    /// <summary>
    /// Turns a <see cref="Query"/> into an immutable <see cref="PlanTemplate"/>: egglog's query planner
    /// (core-relations/src/free_join/plan.rs).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Two phases. First an optional tree decomposition: min-fill-style variable elimination produces bags, redundant
    /// bags are merged (subsumed bags, then ears), and the rest are forced into a chain evaluated Yannakakis-style, each
    /// bag materialized keyed by its message variables, with a result block that re-expands the value variables. Then
    /// join planning inside each bag with one of three strategies (<see cref="PlanStrategy"/>).
    /// </para>
    /// <para>
    /// A pure function of the query, the statistics and the knobs: it never modifies the query (it works on deep copies
    /// of the variable infos) and, given the same statistics, always returns the same plan, because every iteration is
    /// in ascending id order and every tie-break is explicit. Cold path: roughly O(V² · A · arity) for decomposition.
    /// </para>
    /// <para>
    /// egglog defects fixed here (map §8.1): row 5 (a decomposed plan could lose a join constraint,
    /// <see cref="BuildResultBlock"/>), row 6 (<c>all_children[1..]</c> orphaned a child, and the continuation's
    /// pathwidth score unioned the wrong bags, <see cref="TopologicallySortBags"/>), row 7 (Free Join with
    /// decomposition and MinCover with a variable-free atom panicked, <see cref="GetNextFreeJoinStage"/> and
    /// <see cref="BucketQueue"/>), and row 2 on the planner's side (variable-free atoms get an explicit
    /// <see cref="ExistenceCheck"/> and keep their headers under decomposition).
    /// </para>
    /// </remarks>
    internal static partial class QueryPlanner
    {

        /// <summary>
        /// Plans <paramref name="query"/> with its own strategy and decomposition setting. egglog's <c>plan_query</c>
        /// (core-relations/src/free_join/plan.rs:1183-1192).
        /// </summary>
        /// <param name="query">The query; not modified.</param>
        /// <param name="statistics">The statistics that steer cover order and elimination order.</param>
        /// <returns>The plan template.</returns>
        public static PlanTemplate PlanQuery(Query query, IPlanStatistics statistics)
        {
            ArgumentNullException.ThrowIfNull(query);
            return PlanQuery(query, statistics, query.Strategy, query.NoDecomposition);
        }

        /// <summary>
        /// Plans <paramref name="query"/>. egglog's <c>plan_query</c> plus <c>tree_decompose_and_plan</c>
        /// (core-relations/src/free_join/plan.rs:1103-1192).
        /// </summary>
        /// <param name="query">The query; not modified.</param>
        /// <param name="statistics">The statistics that steer cover order and elimination order.</param>
        /// <param name="strategy">The stage-ordering strategy.</param>
        /// <param name="noDecomposition">Whether to skip tree decomposition.</param>
        /// <returns>The plan template.</returns>
        public static PlanTemplate PlanQuery(Query query, IPlanStatistics statistics, PlanStrategy strategy, bool noDecomposition)
        {
            ArgumentNullException.ThrowIfNull(query);
            ArgumentNullException.ThrowIfNull(statistics);
            if (strategy is not (PlanStrategy.Gj or PlanStrategy.PureSize or PlanStrategy.MinCover))
                throw new ArgumentOutOfRangeException(nameof(strategy));

            var sizes = new long[query.Atoms.IdCount];
            foreach (var (atom, info) in query.Atoms)
            {
                if (info.IsDummy)
                    throw new ArgumentException($"Atom {atom} names the dummy table.", nameof(query));

                sizes[atom.Index] = Math.Max(0, statistics.GetApproximateSize(atom, info));
            }

            var ctx = new PlanningContext(PlanningContext.CloneVariables(query.Variables), query.Atoms.Clone(), query.FunDeps, statistics, sizes, query.Variables.IdCount);
            return TreeDecomposeAndPlan(ctx, strategy, query.Action, noDecomposition);
        }

        /// <summary>
        /// The main entry point: decompose when worthwhile, else plan the whole query as one bag. egglog's
        /// <c>tree_decompose_and_plan</c> (core-relations/src/free_join/plan.rs:1103-1181).
        /// </summary>
        /// <param name="ctx">The whole query.</param>
        /// <param name="strategy">The strategy.</param>
        /// <param name="action">The action the plan feeds.</param>
        /// <param name="noDecomposition">Whether to skip decomposition.</param>
        /// <returns>The plan template.</returns>
        static PlanTemplate TreeDecomposeAndPlan(PlanningContext ctx, PlanStrategy strategy, ActionId action, bool noDecomposition)
        {
            var checks = CollectExistenceChecks(ctx);
            if (noDecomposition || ctx.Atoms.Count <= 2)
                return PlanFastPath(ctx, strategy, action, checks);

            // Step 1: decompose the query into tree-structured bags.
            var bags = DecomposeIntoBags(ctx);
            if (bags.Count <= 1)
                return PlanFastPath(ctx, strategy, action, checks);

            // Step 2: sort the bags into a chain, merging the bags that do not continue it.
            bags = TopologicallySortBags(bags);
            if (bags.Count <= 1)
                return PlanFastPath(ctx, strategy, action, checks);

            // Step 3: count in how many bags each variable occurs: message variable or private.
            var usage = CountVariableUsagePerBag(bags, ctx.VariableCapacity);
            var contributed = new bool[bags.Count];

            // Step 4: plan each bag into a materializing block. An atom shared by several bags has the same fast
            // constraints in each, so its header is kept once (egglog pushes one copy per bag, plan.rs:1158; the copies
            // are redundant intersections). Headers are slotted by atom so the template lists them in atom order, as
            // PlanTemplate.Headers promises, rather than in bag order.
            var blocks = new List<PlanBlock>(bags.Count);
            var headerByAtom = new JoinHeaderSpec?[ctx.Atoms.IdCount];
            foreach (var bag in bags)
            {
                var (bagHeaders, stages, spec) = PlanSingleBag(bag, blocks, contributed, usage, strategy);
                blocks.Add(new PlanBlock(stages, spec));
                foreach (var h in bagHeaders)
                    headerByAtom[h.Atom.Index] ??= h;
            }

            // Fix (core-relations/src/free_join/plan.rs:498-506, 591-596): atoms without variables belong to no bag, so
            // egglog's decomposed plan drops their headers. Keep them; their existence checks are in `checks`.
            foreach (var check in checks)
            {
                var info = ctx.Atoms[check.Atom];
                if (info.FastConstraints.Length > 0)
                    headerByAtom[check.Atom.Index] ??= new JoinHeaderSpec(check.Atom, (Constraint[])info.FastConstraints.Clone());
            }

            var headers = new List<JoinHeaderSpec>();
            foreach (var h in headerByAtom)
                if (h is not null)
                    headers.Add(h);

            // Step 5: the result block.
            var result = BuildResultBlock(blocks, contributed);

            // egglog's fuse_last_stage (plan.rs:1030-1059) is disabled there too (plan.rs:1164-1165).
            var lifted = new PlanBlock[blocks.Count];
            for (var i = 0; i < blocks.Count; i++)
                lifted[i] = new PlanBlock(LoopLifting(blocks[i].Stages), blocks[i].Spec);

            return new DecomposedPlan(ctx.Atoms, ctx.VariableCapacity, headers.ToArray(), checks, lifted, LoopLifting(result), action);
        }

        /// <summary>
        /// Plans the whole query as one bag. egglog's <c>fast_path!</c> (core-relations/src/free_join/plan.rs:1109-1123).
        /// </summary>
        static SinglePlan PlanFastPath(PlanningContext ctx, PlanStrategy strategy, ActionId action, ExistenceCheck[] checks)
        {
            var (headers, stages) = PlanStages(ctx, strategy);
            return new SinglePlan(ctx.Atoms, ctx.VariableCapacity, headers, checks, stages.ToArray(), action);
        }

        /// <summary>
        /// Lists the atoms without variables, which no stage scans (map §8.1 row 2).
        /// </summary>
        static ExistenceCheck[] CollectExistenceChecks(PlanningContext ctx)
        {
            var checks = new List<ExistenceCheck>();
            foreach (var (atom, info) in ctx.Atoms)
                if (info.HasNoVariables)
                    checks.Add(new ExistenceCheck(atom, (Constraint[])info.SlowConstraints.Clone()));

            return checks.ToArray();
        }

    }

}

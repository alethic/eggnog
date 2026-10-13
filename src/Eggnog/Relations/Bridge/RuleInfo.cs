using System;
using System.Threading;

namespace Eggnog.Relations
{

    /// <summary>
    /// What an <see cref="EGraph"/> keeps about a registered rule: egglog-bridge's <c>RuleInfo</c>
    /// (egglog-bridge/src/lib.rs:966-972), split for colored contexts: the recorded query, its plan once the rule is
    /// first planned, its description, its generation, and whether it may inherit a watermark. The per-context
    /// semi-naive progress (egglog's <c>last_run_at</c>) lives in each <see cref="EGraphContext"/> as a
    /// <see cref="RuleProgress"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The plan is built once, by whichever context first runs or pre-plans the rule, on that context's statistics, and
    /// shared by every context: <see cref="GetOrBuildCachedPlan"/> double-checks under the rule's plan gate, so
    /// concurrent children of a frozen parent plan a rule once. The plan is immutable (<see cref="CachedPlanInfo"/>).
    /// </para>
    /// <para>
    /// <see cref="AllowInheritance"/> is computed with the plan: a naive rule never inherits, and a semi-naive rule
    /// inherits only when its lowered actions are query-determined (<see cref="ActionReadAnalysis"/>). The bridge has
    /// no split or callback rules; the rule layer runs those as naive rules.
    /// </para>
    /// <para>
    /// Thread-safe: the plan and the flag are published with a volatile write; everything else is immutable.
    /// </para>
    /// </remarks>
    internal sealed class RuleInfo
    {

        readonly object planGate = new();
        readonly EGraph? owner;
        CachedPlanInfo? cachedPlan;
        volatile bool allowInheritance;

        /// <summary>
        /// Initializes a rule that belongs to no e-graph, with generation 1: for tests that plan a recorded query
        /// themselves. <see cref="LastRunAt"/> is then always zero.
        /// </summary>
        /// <param name="query">The recorded rule.</param>
        /// <param name="description">The rule's description, which keys its reports.</param>
        public RuleInfo(BridgeQuery query, string description)
            : this(query, description, null, 1)
        {

        }

        /// <summary>
        /// Initializes a rule of <paramref name="owner"/> that has never run.
        /// </summary>
        /// <param name="query">The recorded rule.</param>
        /// <param name="description">The rule's description, which keys its reports.</param>
        /// <param name="owner">The e-graph, or <c>null</c>.</param>
        /// <param name="generation">The generation of the rule's id (<see cref="RuleTable.Reserve"/>); at least 1.</param>
        internal RuleInfo(BridgeQuery query, string description, EGraph? owner, int generation)
        {
            ArgumentNullException.ThrowIfNull(query);
            ArgumentNullException.ThrowIfNull(description);
            ArgumentOutOfRangeException.ThrowIfLessThan(generation, 1);
            Query = query;
            Description = description;
            this.owner = owner;
            Generation = generation;
        }

        /// <summary>
        /// Gets the rule's id.
        /// </summary>
        public EGraphRuleId Id => Query.RuleId;

        /// <summary>
        /// Gets the generation of the rule's id: progress recorded for a freed rule whose id this rule reuses has
        /// another generation and is ignored.
        /// </summary>
        public int Generation { get; }

        /// <summary>
        /// Gets the timestamp of the rule's last successful run in the e-graph's root context, zero if it never ran
        /// there: egglog's <c>last_run_at</c>, read from the root's <see cref="RuleProgress"/>. A view kept for the
        /// root's egglog-compatible API; each context keeps its own progress.
        /// </summary>
        public uint LastRunAt => owner?.Root.CommittedLastRunAt(Id, Generation) ?? 0;

        /// <summary>
        /// Gets the recorded rule.
        /// </summary>
        public BridgeQuery Query { get; }

        /// <summary>
        /// Gets the rule's plan, built at its first run or by a freeze that pre-plans it; <c>null</c> before. egglog's
        /// <c>cached_plan</c>. A volatile read.
        /// </summary>
        public CachedPlanInfo? CachedPlan => Volatile.Read(ref cachedPlan);

        /// <summary>
        /// Gets whether the rule may seed its watermark in a context from an ancestor's: it is semi-naive and its
        /// actions are query-determined. <c>false</c> until the rule is planned.
        /// </summary>
        public bool AllowInheritance => allowInheritance;

        /// <summary>
        /// Gets the rule's description, which keys its reports (every variant of the rule shares it).
        /// </summary>
        public string Description { get; }

        /// <summary>
        /// Gets the rule's plan, building it on <paramref name="database"/>'s statistics the first time: egglog's
        /// planning at a rule's first run (lib.rs:1404-1409). Double-checked under the rule's plan gate, so a rule is
        /// planned once whatever the number of contexts and threads that ask; the plan is shared by all of them.
        /// </summary>
        /// <remarks>
        /// Computes <see cref="AllowInheritance"/> with the plan, from the plan's lowered actions and the store's schema
        /// (<see cref="ActionReadAnalysis"/>); a rule planned on a database without a store never inherits. When the
        /// e-graph has a contradiction function, a head insert into it is followed by the early-stop call
        /// (<see cref="StopOnContradictionFunction"/>). Cold path.
        /// </remarks>
        /// <param name="database">The database of the context that plans the rule; its views must be current in a
        /// child.</param>
        /// <returns>The plan.</returns>
        /// <exception cref="QueryException">The core rejects the rule.</exception>
        public CachedPlanInfo GetOrBuildCachedPlan(Database database)
        {
            ArgumentNullException.ThrowIfNull(database);
            if (Volatile.Read(ref cachedPlan) is { } existing)
                return existing;

            lock (planGate)
            {
                if (cachedPlan is { } planned)
                    return planned;

                var plan = Query.BuildCachedPlan(database, Description, owner?.ContradictionHook);
                var inherit = Query.SemiNaive
                    && database.Owner is { } context
                    && ActionReadAnalysis.IsQueryDetermined(plan.Plan.Actions, context.Store.Schema, database.ExternalFunctions);
                allowInheritance = inherit;
                Volatile.Write(ref cachedPlan, plan);
                return plan;
            }
        }

        /// <inheritdoc />
        public override string ToString() => $"{Description} (last run at {LastRunAt}{(CachedPlan is null ? ", not planned" : "")})";

    }

}

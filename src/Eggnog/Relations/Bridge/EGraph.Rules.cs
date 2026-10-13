using System;

namespace Eggnog.Relations
{

    internal sealed partial class EGraph
    {

        /// <summary>
        /// The registered rules, by id. egglog's <c>rules</c>, a <c>DenseIdMapWithReuse</c> (lib.rs:120): an id is
        /// reserved when a builder starts and filled when it builds; each reservation of an id has a new generation
        /// (colored-contexts spec §10.2).
        /// </summary>
        readonly RuleTable rules = new();

        /// <summary>
        /// Gets the number of registered rules.
        /// </summary>
        internal int RuleCount => rules.Count;

        /// <summary>
        /// Gets the registered rules.
        /// </summary>
        internal RuleTable Rules => rules;

        /// <summary>
        /// Starts a rule. egglog's <c>new_rule</c> (egglog-bridge/src/rule.rs:131-156); if unsure, egglog suggests
        /// <c>NewRule("", true)</c>.
        /// </summary>
        /// <remarks>
        /// The rule's id is reserved now (a freed id is reused), so a builder that is never built keeps its id reserved,
        /// as in egglog. A semi-naive rule's runs consider only matches that involve a row new since its last run in the
        /// running context; a rule that is not semi-naive considers every match on every run (egglog's <c>:naive</c> rules
        /// and one-shot commands). Rules are definitions of the e-graph, not of a context: every context may run every
        /// rule. Thread-safe (rule-layer D6).
        /// </remarks>
        /// <param name="description">The rule's description, which keys its reports.</param>
        /// <param name="semiNaive">Whether runs consider only matches involving new rows.</param>
        /// <returns>The builder.</returns>
        public EGraphRuleBuilder NewRule(string description, bool semiNaive)
        {
            ArgumentNullException.ThrowIfNull(description);
            var (id, generation) = rules.Reserve();
            return new EGraphRuleBuilder(this, description, new BridgeQuery(ufTable, idCounter, timestampCounter, id, semiNaive), generation);
        }

        /// <summary>
        /// Removes a rule; its id may be handed out again, with a new generation, so no context applies the freed rule's
        /// progress to the new one (§13 edge 29). egglog's <c>free_rule</c> (egglog-bridge/src/rule.rs:158-161). An
        /// unknown id is ignored. Thread-safe.
        /// </summary>
        /// <remarks>
        /// Defect fixed (egglog-bridge/src/rule.rs:159-161, 397, 584): egglog keeps the lazy panic functions a rule's
        /// lookups and external calls created, so the frontend's one-shot rules (<c>check</c>, <c>eval</c>), built and
        /// freed per command, grow the external function registry without bound. Here they belong to the rule and are
        /// freed with it.
        /// </remarks>
        /// <param name="rule">The rule.</param>
        public void FreeRule(EGraphRuleId rule)
        {
            if (rules.Remove(rule, out var info) == false)
                return;

            foreach (var function in info.Query.OwnedFunctions)
                FreeExternalFunction(function);
        }

        /// <summary>
        /// Gets what the e-graph keeps about <paramref name="rule"/>. Lock-free; any thread.
        /// </summary>
        /// <param name="rule">The rule.</param>
        /// <returns>The rule's information.</returns>
        /// <exception cref="ArgumentException">No such rule.</exception>
        internal RuleInfo GetRuleInfo(EGraphRuleId rule)
        {
            if (rules.TryGet(rule, out var info))
                return info;

            throw new ArgumentException($"Rule {rule} is not registered in this e-graph.", nameof(rule));
        }

        /// <summary>
        /// Creates a rule entry for the base value <paramref name="value"/>: its boxed value, typed with its base value
        /// type. egglog's <c>base_value_constant</c> (egglog-bridge/src/lib.rs:264-273). Registers nothing: the type must
        /// be registered already.
        /// </summary>
        /// <typeparam name="TCodec">The codec of the base value type.</typeparam>
        /// <typeparam name="T">The base value type.</typeparam>
        /// <param name="value">The value.</param>
        /// <returns>The constant entry.</returns>
        public RuleEntry BaseValueConstant<TCodec, T>(T value)
            where TCodec : struct, IBaseValueCodec<T>
            where T : notnull, IEquatable<T>
        {
            var values = db.BaseValues;
            return RuleEntry.Const(values.Get<TCodec, T>(value), ColumnTy.Base(values.GetTypeId<TCodec, T>()));
        }

        /// <summary>
        /// Runs the given rules once at the root, as one iteration, and rebuilds. egglog's <c>run_rules</c>
        /// (lib.rs:583-624, 1397-1420), on <see cref="Root"/> (<see cref="EGraphContext.RunRules"/>) without its
        /// <c>EnsureClean</c> rebuild, so the root keeps egglog's numbering whatever was merged raw before.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Each rule is planned at its first run (with the statistics of the tables at that time; the plan is never
        /// revisited). Then one rule set is built at the current timestamp <c>T</c>: each rule contributes its semi-naive
        /// variants for its watermark (<see cref="BridgeQuery.AddRulesFromCached(RuleSetBuilder, uint, CachedPlanInfo, uint[])"/>), and
        /// a rule listed again runs with <c>T</c>. Every rule matches against the same snapshot; their writes, all stamped
        /// <c>T</c>, merge at the end, and the iteration then advances the timestamp, or rebuilds if the run made an
        /// effective union, with the first rebuild pass at <c>T</c>. Rows written by the run or its rebuild are therefore
        /// new to the next run. The rules' watermarks become <c>T</c> once the run has merged without a recorded failure
        /// (X18).
        /// </para>
        /// <para>
        /// The report's <see cref="IterationReport.Changed"/> is whether the rules' writes changed the database; what the
        /// rebuild changed is reported separately (decision 11). Match counts are in the rule set report, by description
        /// (every variant of a rule shares its rule's description).
        /// </para>
        /// <para>
        /// A panic recorded by an action throws <see cref="PanicException"/> after the run's writes merged and the
        /// iteration's bookkeeping finished, with the rules' watermarks left as they were (X18: egglog advances them).
        /// </para>
        /// <para>
        /// Any other exception thrown while the rules run (an external function throwing, say) undoes the run: the
        /// database discards every write the run staged (<see cref="Database.ExecuteRuleSet"/>), a panic the run recorded
        /// is dropped, and the rules keep their watermarks, so the e-graph is as before the run (bar fresh ids minted and
        /// values interned, which nothing refers to) and the next run finds the matches again, once. An exception thrown
        /// while the run's writes merge (a merge primitive throwing) cannot be undone: the merge stays partial, the
        /// watermarks advance, so no match is applied twice, and the writes not merged yet are merged by the next flush or
        /// run, except those of the table whose merge threw, which it drops (<see cref="SortedWritesTable"/>). In both
        /// cases the timestamp does not advance and nothing is rebuilt.
        /// </para>
        /// </remarks>
        /// <param name="rules">The rules; a rule listed twice runs its plan again with the new watermark.</param>
        /// <returns>The report.</returns>
        /// <exception cref="ArgumentException">A rule is not registered.</exception>
        /// <exception cref="PanicException">A panic was recorded.</exception>
        public IterationReport RunRules(params ReadOnlySpan<EGraphRuleId> rules) => root.RunRulesCore(rules, egglogCompatible: true);

        /// <summary>
        /// Records a built rule under its reserved id, with the reservation's generation. Called by
        /// <see cref="EGraphRuleBuilder.Build"/>.
        /// </summary>
        /// <param name="query">The recorded rule, whose <see cref="BridgeQuery.RuleId"/> is reserved.</param>
        /// <param name="description">The rule's description.</param>
        /// <param name="generation">The reservation's generation.</param>
        /// <returns>The rule.</returns>
        internal RuleInfo InsertRule(BridgeQuery query, string description, int generation)
        {
            ArgumentNullException.ThrowIfNull(query);
            var info = new RuleInfo(query, description, this, generation);
            rules.Publish(query.RuleId, info);
            return info;
        }

    }

}

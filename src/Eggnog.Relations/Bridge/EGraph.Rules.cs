using System;
using System.Collections.Generic;

namespace Eggnog.Relations
{

    public sealed partial class EGraph
    {

        /// <summary>
        /// The registered rules, by id. egglog's <c>rules</c>, a <c>DenseIdMapWithReuse</c> (lib.rs:120): an id is
        /// reserved when a builder starts and filled when it builds.
        /// </summary>
        readonly DenseIdMap<EGraphRuleId, RuleInfo> rules = new();

        /// <summary>
        /// The ids of freed rules, reused last-freed first.
        /// </summary>
        readonly List<EGraphRuleId> freeRuleIds = new();

        /// <summary>
        /// Gets the number of registered rules.
        /// </summary>
        internal int RuleCount => rules.Count;

        /// <summary>
        /// Starts a rule. egglog's <c>new_rule</c> (egglog-bridge/src/rule.rs:131-156); if unsure, egglog suggests
        /// <c>NewRule("", true)</c>.
        /// </summary>
        /// <remarks>
        /// The rule's id is reserved now (a freed id is reused), so a builder that is never built keeps its id reserved,
        /// as in egglog. A semi-naive rule's runs consider only matches that involve a row new since its last run; a
        /// rule that is not semi-naive considers every match on every run (egglog's <c>:naive</c> rules and one-shot
        /// commands).
        /// </remarks>
        /// <param name="description">The rule's description, which keys its reports.</param>
        /// <param name="semiNaive">Whether runs consider only matches involving new rows.</param>
        /// <returns>The builder.</returns>
        public EGraphRuleBuilder NewRule(string description, bool semiNaive)
        {
            ArgumentNullException.ThrowIfNull(description);
            var id = ReserveRuleSlot();
            return new EGraphRuleBuilder(this, description, new BridgeQuery(ufTable, idCounter, timestampCounter, id, semiNaive));
        }

        /// <summary>
        /// Removes a rule; its id may be handed out again. egglog's <c>free_rule</c> (egglog-bridge/src/rule.rs:158-161).
        /// An unknown id is ignored.
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
            if (rules.TryTake(rule, out var info) == false)
                return;

            freeRuleIds.Add(rule);
            foreach (var function in info.Query.OwnedFunctions)
                FreeExternalFunction(function);
        }

        /// <summary>
        /// Gets what the e-graph keeps about <paramref name="rule"/>.
        /// </summary>
        /// <param name="rule">The rule.</param>
        /// <returns>The rule's information.</returns>
        /// <exception cref="ArgumentException">No such rule.</exception>
        internal RuleInfo GetRuleInfo(EGraphRuleId rule)
        {
            if (rules.TryGetValue(rule, out var info))
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
        /// Runs the given rules once, as one iteration, and rebuilds. egglog's <c>run_rules</c> (lib.rs:583-624,
        /// 1397-1420).
        /// </summary>
        /// <remarks>
        /// <para>
        /// Each rule is planned at its first run (with the statistics of the tables at that time; the plan is never
        /// revisited). Then one rule set is built at the current timestamp <c>T</c>: each rule contributes its semi-naive
        /// variants for its watermark (<see cref="BridgeQuery.AddRulesFromCached"/>) and records <c>T</c> as its new
        /// watermark. Every rule matches against the same snapshot; their writes, all stamped <c>T</c>, merge at the end,
        /// and the iteration then advances the timestamp, or rebuilds if the run made an effective union, with the first
        /// rebuild pass at <c>T</c> (<see cref="RunIteration"/>). Rows written by the run or its rebuild are therefore new
        /// to the next run.
        /// </para>
        /// <para>
        /// The report's <see cref="IterationReport.Changed"/> is whether the rules' writes changed the database; what the
        /// rebuild changed is reported separately (decision 11). Match counts are in the rule set report, by description
        /// (every variant of a rule shares its rule's description).
        /// </para>
        /// <para>
        /// A panic recorded by an action throws <see cref="PanicException"/> after the run's writes merged and the
        /// iteration's bookkeeping finished, with the rules' watermarks advanced, as in egglog.
        /// </para>
        /// <para>
        /// Any other exception thrown while the rules run (an external function throwing, say) undoes the run: the
        /// database discards every write the run staged (<see cref="Database.ExecuteRuleSet"/>), a panic the run recorded
        /// is dropped, and the rules get their watermarks back, so the e-graph is as before the run (bar fresh ids minted
        /// and values interned, which nothing refers to) and the next run finds the matches again, once. An exception
        /// thrown while the run's writes merge (a merge primitive throwing) cannot be undone: the merge stays partial, the
        /// watermarks stay advanced, so no match is applied twice, and the writes not merged yet are merged by the next
        /// flush or run, except those of the table whose merge threw, which it drops (<see cref="SortedWritesTable"/>).
        /// In both cases the timestamp does not advance and nothing is rebuilt.
        /// </para>
        /// </remarks>
        /// <param name="rules">The rules; a rule listed twice runs its plan again with the new watermark.</param>
        /// <returns>The report.</returns>
        /// <exception cref="ArgumentException">A rule is not registered.</exception>
        /// <exception cref="PanicException">A panic was recorded.</exception>
        public IterationReport RunRules(params ReadOnlySpan<EGraphRuleId> rules)
        {
            var infos = new RuleInfo[rules.Length];
            for (var i = 0; i < rules.Length; i++)
                infos[i] = GetRuleInfo(rules[i]);

            // Plan every rule that has not run yet (lib.rs:1404-1409).
            foreach (var info in infos)
                info.CachedPlan ??= info.Query.BuildCachedPlan(db, info.Description);

            return RunIteration(timestamp => RunRulesImpl(infos, timestamp));
        }

        /// <summary>
        /// Builds the rule set of one run and runs it: egglog's <c>run_rules_impl</c> (lib.rs:1397-1420), after planning.
        /// The rules run and their writes merge in two steps (<see cref="Database.ExecuteRuleSet"/>,
        /// <see cref="Database.MergeRuleSet"/>), so that a failure of the rules, which the database undoes, restores the
        /// watermarks, and a failure of the merge, which it cannot undo, keeps them (see <see cref="RunRules"/>).
        /// </summary>
        /// <param name="infos">The rules, planned.</param>
        /// <param name="timestamp">The run's timestamp.</param>
        /// <returns>The rule set's report.</returns>
        RuleSetReport RunRulesImpl(RuleInfo[] infos, uint timestamp)
        {
            var watermarks = new uint[infos.Length];
            var added = 0;
            var hadPanic = panicMessage.HasValue;
            var ruleSet = default(RuleSet);
            try
            {
                RuleSetReport executed;
                try
                {
                    var builder = db.NewRuleSet();
                    for (; added < infos.Length; added++)
                    {
                        var info = infos[added];
                        watermarks[added] = info.LastRunAt;
                        info.Query.AddRulesFromCached(builder, info.LastRunAt, info.CachedPlan!);
                        info.LastRunAt = timestamp;
                    }

                    ruleSet = builder.Build();
                    executed = db.ExecuteRuleSet(ruleSet, ReportLevel, timestamp);
                }
                catch
                {
                    // Nothing the run staged survives, so the rules get their watermarks back, in reverse so that a rule
                    // listed twice gets its first one, and a panic an action recorded before the exception is dropped.
                    for (var i = added - 1; i >= 0; i--)
                        infos[i].LastRunAt = watermarks[i];

                    if (hadPanic == false)
                        panicMessage.TryTake(out _);

                    throw;
                }

                return db.MergeRuleSet(ruleSet, executed, timestamp);
            }
            finally
            {
                ruleSet?.Dispose();
            }
        }

        /// <summary>
        /// Records a built rule. Called by <see cref="EGraphRuleBuilder.Build"/>.
        /// </summary>
        /// <param name="rule">The rule's reserved id.</param>
        /// <param name="info">The rule.</param>
        internal void InsertRule(EGraphRuleId rule, RuleInfo info)
        {
            ArgumentNullException.ThrowIfNull(info);
            rules.Insert(rule, info);
        }

        /// <summary>
        /// Reserves the id of a new rule: the most recently freed one, or the next unused one. egglog's
        /// <c>reserve_slot</c> (numeric-id/src/lib.rs:322-331).
        /// </summary>
        EGraphRuleId ReserveRuleSlot()
        {
            if (freeRuleIds.Count > 0)
            {
                var reused = freeRuleIds[^1];
                freeRuleIds.RemoveAt(freeRuleIds.Count - 1);
                return reused;
            }

            var id = rules.NextId;
            rules.ReserveSpace(id.Index);
            return id;
        }

    }

}

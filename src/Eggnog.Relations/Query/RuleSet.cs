using System;
using System.Diagnostics;

namespace Eggnog.Relations
{

    /// <summary>
    /// A set of rules to run against a <see cref="Database"/>: for each rule its plan, header subsets, description and
    /// names, and for each action its program. egglog's <c>RuleSet</c> (core-relations/src/query.rs:49-80), built by
    /// <see cref="RuleSetBuilder"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Rules and actions are in bijection: rule <c>r</c> feeds action <c>ActionId(r.Index)</c>, because the builder adds
    /// exactly one action with each rule (egglog relies on this to attribute match counts to rules). The header subsets
    /// were evaluated against the tables as they were when each rule was added; running the set after the tables change
    /// reads stale subsets, as in egglog, so build a rule set right before running it.
    /// </para>
    /// <para>
    /// Immutable apart from <see cref="Dispose"/>, which returns the header subsets' pooled buffers; reads are safe from
    /// any number of threads.
    /// </para>
    /// </remarks>
    internal sealed class RuleSet : IDisposable
    {

        readonly IdVec<RuleId, RulePlan> rules;
        readonly DenseIdMap<ActionId, ActionInfo> actions;

        /// <summary>
        /// Initializes a new instance. The collections are owned by the new instance.
        /// </summary>
        /// <param name="rules">The rules.</param>
        /// <param name="actions">The actions, one per rule, with the same indexes.</param>
        public RuleSet(IdVec<RuleId, RulePlan> rules, DenseIdMap<ActionId, ActionInfo> actions)
        {
            ArgumentNullException.ThrowIfNull(rules);
            ArgumentNullException.ThrowIfNull(actions);
            if (rules.Count != actions.IdCount)
                throw new ArgumentException("Every rule needs exactly one action.", nameof(actions));

            this.rules = rules;
            this.actions = actions;
            for (var i = 0; i < rules.Count; i++)
                Debug.Assert(rules[new RuleId(i)].Action.Index == i && actions.ContainsKey(new ActionId(i)), "rule and action ids must stand in a bijection");
        }

        /// <summary>
        /// Gets the number of rules (and of actions).
        /// </summary>
        public int Count => rules.Count;

        /// <summary>
        /// Gets whether the set has no rules; running it does nothing (egglog returns an empty report).
        /// </summary>
        public bool IsEmpty => rules.Count == 0;

        /// <summary>
        /// Gets the rules, indexed by <see cref="RuleId"/>. The span must not be written.
        /// </summary>
        public ReadOnlySpan<RulePlan> Rules => rules.AsSpan();

        /// <summary>
        /// Gets rule <paramref name="rule"/>.
        /// </summary>
        /// <param name="rule">The rule.</param>
        /// <returns>The rule's plan.</returns>
        /// <exception cref="ArgumentOutOfRangeException">No such rule.</exception>
        public RulePlan this[RuleId rule]
        {
            get
            {
                if ((uint)rule.Index >= (uint)rules.Count)
                    throw new ArgumentOutOfRangeException(nameof(rule), rule, "The rule is not part of this rule set.");

                return rules[rule];
            }
        }

        /// <summary>
        /// Gets the program of action <paramref name="action"/>.
        /// </summary>
        /// <param name="action">The action.</param>
        /// <returns>The program.</returns>
        /// <exception cref="ArgumentOutOfRangeException">No such action.</exception>
        public ActionInfo GetAction(ActionId action)
        {
            if (actions.TryGetValue(action, out var info) == false)
                throw new ArgumentOutOfRangeException(nameof(action), action, "The action is not part of this rule set.");

            return info;
        }

        /// <summary>
        /// Snapshots rule <paramref name="rule"/> for reuse in other rule sets. egglog's <c>build_cached_plan</c>
        /// (core-relations/src/query.rs:66-79). O(1): the template, the program, the names and the constant lists are
        /// shared, not copied.
        /// </summary>
        /// <remarks>
        /// The cached plan keeps the rule's headers as constraints, not as subsets: the template's headers, plus, for a
        /// rule that was itself added from a cached plan, the extra constraints it was added with (egglog clones that
        /// rule's plan, whose headers include them).
        /// </remarks>
        /// <param name="rule">The rule.</param>
        /// <returns>The cached plan.</returns>
        /// <exception cref="ArgumentOutOfRangeException">No such rule.</exception>
        public CachedPlan BuildCachedPlan(RuleId rule)
        {
            var plan = this[rule];
            return new CachedPlan(plan.Template, plan.Description, plan.Symbols, GetAction(plan.Action), plan.IdConstants, plan.ExtraConstraints);
        }

        /// <summary>
        /// Returns the header subsets' pooled buffers. Idempotent; the rule set must not be run afterwards.
        /// </summary>
        public void Dispose()
        {
            foreach (var rule in rules.AsSpan())
                rule.Instance?.Dispose();
        }

    }

}

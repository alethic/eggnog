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
        public RuleSet(IdVec<RuleId, RulePlan> rules, DenseIdMap<ActionId, ActionInfo> actions) :
            this(rules, actions, null, null, 0, 0)
        {

        }

        /// <summary>
        /// Initializes a rule set built on <paramref name="view"/>: its rules' headers were evaluated at
        /// <paramref name="viewGeneration"/> and <paramref name="viewStamp"/>, and they read the pinned views
        /// <paramref name="views"/>. The collections are owned by the new instance.
        /// </summary>
        /// <param name="rules">The rules.</param>
        /// <param name="actions">The actions, one per rule, with the same indexes.</param>
        /// <param name="view">The view the rule set was built on, or <c>null</c> for one assembled by hand.</param>
        /// <param name="views">The views of every table the rules read, or <c>null</c>.</param>
        /// <param name="viewGeneration">The view's generation when the first header was evaluated.</param>
        /// <param name="viewStamp">The view's <see cref="ContextView.Stamp"/> when the first header was evaluated.</param>
        internal RuleSet(IdVec<RuleId, RulePlan> rules, DenseIdMap<ActionId, ActionInfo> actions, ContextView? view, ViewSnapshot? views, long viewGeneration, long viewStamp)
        {
            ArgumentNullException.ThrowIfNull(rules);
            ArgumentNullException.ThrowIfNull(actions);
            View = view;
            Views = views;
            ViewGeneration = viewGeneration;
            ViewStamp = viewStamp;
            if (rules.Count != actions.IdCount)
                throw new ArgumentException("Every rule needs exactly one action.", nameof(actions));

            this.rules = rules;
            this.actions = actions;
            for (var i = 0; i < rules.Count; i++)
                Debug.Assert(rules[new RuleId(i)].Action.Index == i && actions.ContainsKey(new ActionId(i)), "rule and action ids must stand in a bijection");
        }

        /// <summary>
        /// Gets the view the rule set was built on (<see cref="Database.View"/>), or <c>null</c> for a rule set assembled
        /// by hand from rules of a root database. <see cref="Database.RunRuleSet"/> runs it only on that view's database.
        /// </summary>
        public ContextView? View { get; }

        /// <summary>
        /// Gets the views of every table the rules' atoms and lookups read, pinned when the rule set was built: the run
        /// reads only these. <c>null</c> for a rule set assembled by hand, which a root run pins when it starts.
        /// </summary>
        public ViewSnapshot? Views { get; }

        /// <summary>
        /// Gets the view's <see cref="ContextView.Generation"/> when the first header of the rule set was evaluated (or
        /// when it was built, for a rule set without rules); a child's run checks it has not moved, so its headers and trie
        /// roots read the same views. <see cref="Views"/> is pinned at the build, so a generation that moved between
        /// the first header and the build is caught too.
        /// </summary>
        public long ViewGeneration { get; }

        /// <summary>
        /// Gets the view's <see cref="ContextView.Stamp"/> when the first header was evaluated: a child's run checks it has
        /// not moved, since header subsets name rows, which a merge of the context or an ancestor can move (a compaction),
        /// remove or add to without changing any view's structure. 0 at a root, which keeps egglog's rule: build a
        /// rule set right before running it.
        /// </summary>
        public long ViewStamp { get; }

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
        /// Gets the actions, by <see cref="ActionId"/>. Callers must not modify the map.
        /// </summary>
        internal DenseIdMap<ActionId, ActionInfo> Actions => actions;

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
        /// rule's plan, whose headers include them). It keeps the written template and program
        /// (<see cref="RulePlan.WrittenTemplate"/>, <see cref="RulePlan.WrittenActions"/>), never a context's canonical
        /// variant.
        /// </remarks>
        /// <param name="rule">The rule.</param>
        /// <returns>The cached plan.</returns>
        /// <exception cref="ArgumentOutOfRangeException">No such rule.</exception>
        public CachedPlan BuildCachedPlan(RuleId rule)
        {
            var plan = this[rule];
            return new CachedPlan(plan.WrittenTemplate, plan.Description, plan.Symbols, plan.WrittenActions ?? GetAction(plan.Action), plan.IdConstants, plan.ExtraConstraints);
        }

        /// <summary>
        /// Gets the table an action instruction looks up, which a child's search reads through the rule set's snapshot:
        /// that of a <see cref="LookupInstr"/>, <see cref="LookupWithDefaultInstr"/>,
        /// <see cref="LookupWithFallbackInstr"/> or <see cref="LookupOrInsertDefaultInstr"/>.
        /// </summary>
        /// <param name="instr">The instruction.</param>
        /// <returns>The table, or <c>null</c> for an instruction that looks nothing up.</returns>
        internal static TableId? LookupTable(Instr instr) => instr switch
        {
            LookupInstr i => i.Table,
            LookupWithDefaultInstr i => i.Table,
            LookupWithFallbackInstr i => i.Table,
            LookupOrInsertDefaultInstr i => i.Table,
            _ => null,
        };

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

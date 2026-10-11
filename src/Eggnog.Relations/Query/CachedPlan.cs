using System;

namespace Eggnog.Relations
{

    /// <summary>
    /// A rule planned once, to be added to many rule sets: egglog's <c>CachedPlan</c>
    /// (core-relations/src/query.rs:35-41), made by <see cref="RuleSet.BuildCachedPlan"/> and consumed by
    /// <see cref="RuleSetBuilder.AddRuleFromCachedPlan"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The pattern egglog's bridge follows: compile each rule into a cached plan when it is declared, then every time
    /// the rule runs, add it to a fresh rule set with the semi-naive timestamp bounds of that run as extra constraints.
    /// Adding re-evaluates the headers against the current tables; the template, the stages and the action program are
    /// never re-planned or copied.
    /// </para>
    /// <para>
    /// Holds no row subsets, so it stays valid however the tables change, and it is immutable: one instance may be
    /// added to any number of rule sets, on any thread, in any context (the executor's per-context header evaluation
    /// and <see cref="IdConstants"/> are what differ between contexts).
    /// </para>
    /// </remarks>
    internal sealed class CachedPlan
    {

        /// <summary>
        /// Initializes a new instance. The arrays are shared with other immutable owners.
        /// </summary>
        /// <param name="plan">The plan template.</param>
        /// <param name="description">The rule's description.</param>
        /// <param name="symbols">The names of the rule's atoms and variables.</param>
        /// <param name="actions">The rule's action program.</param>
        /// <param name="idConstants">The constant ids the rule's atoms match against.</param>
        /// <param name="extraConstraints">Constraints evaluated as headers before the template's own on every
        /// instantiation, after the instantiation's extra constraints.</param>
        public CachedPlan(PlanTemplate plan, string description, SymbolMap symbols, ActionInfo actions, IdConstant[] idConstants, (AtomId Atom, Constraint Constraint)[] extraConstraints)
        {
            ArgumentNullException.ThrowIfNull(plan);
            ArgumentNullException.ThrowIfNull(description);
            ArgumentNullException.ThrowIfNull(symbols);
            ArgumentNullException.ThrowIfNull(actions);
            ArgumentNullException.ThrowIfNull(idConstants);
            ArgumentNullException.ThrowIfNull(extraConstraints);
            Plan = plan;
            Description = description;
            Symbols = symbols;
            Actions = actions;
            IdConstants = idConstants;
            ExtraConstraints = extraConstraints;
        }

        /// <summary>
        /// Gets the plan template.
        /// </summary>
        public PlanTemplate Plan { get; }

        /// <summary>
        /// Gets the rule's description.
        /// </summary>
        public string Description { get; }

        /// <summary>
        /// Gets the names of the rule's atoms and variables.
        /// </summary>
        public SymbolMap Symbols { get; }

        /// <summary>
        /// Gets the rule's action program, shared by every rule added from this plan.
        /// </summary>
        public ActionInfo Actions { get; }

        /// <summary>
        /// Gets the constant ids the rule's atoms match against. Callers must not modify the array.
        /// </summary>
        public IdConstant[] IdConstants { get; }

        /// <summary>
        /// Gets the extra constraints of the rule this plan was cached from, when that rule was itself added from a
        /// cached plan: egglog's cached plan copies that rule's headers, extra ones included, so every instantiation
        /// re-evaluates them (core-relations/src/query.rs:66-79, 174-189). Empty for a rule built from scratch.
        /// Callers must not modify the array.
        /// </summary>
        public (AtomId Atom, Constraint Constraint)[] ExtraConstraints { get; }

    }

}

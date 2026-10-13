using System;
using System.Text;

namespace Eggnog.Relations
{

    /// <summary>
    /// One rule of a <see cref="RuleSet"/>: the plan template, the action it feeds, the header subsets evaluated when
    /// the rule was added, and the rule's description and names. egglog's <c>(Plan, Arc&lt;str&gt;, SymbolMap)</c> entry
    /// of <c>RuleSet::plans</c> (core-relations/src/query.rs:53-63).
    /// </summary>
    /// <remarks>
    /// <para>
    /// egglog's <c>Plan</c> is split in two here (see <see cref="PlanTemplate"/>): the data-independent template, shared
    /// with the rule's <see cref="CachedPlan"/> and every rule set it is added to, and <see cref="Instance"/>, the header
    /// subsets of this rule set. <see cref="Action"/> is the action id in this rule set: the rule's
    /// <see cref="RuleId"/> has the same index, egglog's rules/actions bijection. The template's own
    /// <see cref="PlanTemplate.Action"/> is the action of the rule set the template was planned in, so the executor
    /// must use <see cref="Action"/> (or <see cref="PlanInstance.Action"/>).
    /// </para>
    /// <para>
    /// <see cref="Instance"/> is <c>null</c> when some header selected no rows as the rule was built from scratch: the
    /// rule cannot match, and the executor skips it, as egglog's breaks out of a plan whose header subset is empty
    /// (core-relations/src/free_join/execute.rs:525-528). A rule added from a cached plan always has an instance, since
    /// <see cref="RuleSetBuilder.AddRuleFromCachedPlan"/> adds no rule at all in that case.
    /// </para>
    /// <para>
    /// Immutable; the rule set owns the instance's subsets and disposes them.
    /// </para>
    /// </remarks>
    internal sealed class RulePlan
    {

        /// <summary>
        /// Initializes a new instance. The arrays are owned by (or shared with other immutable owners of) the new
        /// instance.
        /// </summary>
        /// <param name="template">The plan template.</param>
        /// <param name="action">The rule's action id in its rule set.</param>
        /// <param name="instance">The header subsets of this rule set, or <c>null</c> when a header is empty.</param>
        /// <param name="description">The rule's description.</param>
        /// <param name="symbols">The names of the rule's atoms and variables.</param>
        /// <param name="idConstants">The constant ids the rule's atoms match against.</param>
        /// <param name="extraConstraints">The constraints added on top of the template's headers when the rule came
        /// from a cached plan, in header order; empty for a rule built from scratch.</param>
        public RulePlan(PlanTemplate template, ActionId action, PlanInstance? instance, string description, SymbolMap symbols, IdConstant[] idConstants, (AtomId Atom, Constraint Constraint)[] extraConstraints) :
            this(template, action, instance, description, symbols, idConstants, extraConstraints, template, null, null)
        {

        }

        /// <summary>
        /// Initializes a new instance whose template and program may run Id constants other than the written ones
        /// (colored-contexts spec §9.8, X19).
        /// </summary>
        /// <param name="template">The plan template that runs: the written one, or its variant for K.</param>
        /// <param name="action">The rule's action id in its rule set.</param>
        /// <param name="instance">The header subsets of this rule set, or <c>null</c> when a header is empty.</param>
        /// <param name="description">The rule's description.</param>
        /// <param name="symbols">The names of the rule's atoms and variables.</param>
        /// <param name="idConstants">The constant ids the rule's atoms match against, as written.</param>
        /// <param name="extraConstraints">The constraints added on top of the template's headers.</param>
        /// <param name="writtenTemplate">The template as planned, with the written constants.</param>
        /// <param name="writtenActions">The action program as written, or <c>null</c> when the rule set's program for the
        /// rule is it.</param>
        /// <param name="canonicalConstants">K, the executed constants (query constants first), or <c>null</c> when the
        /// rule has none.</param>
        public RulePlan(PlanTemplate template, ActionId action, PlanInstance? instance, string description, SymbolMap symbols, IdConstant[] idConstants, (AtomId Atom, Constraint Constraint)[] extraConstraints, PlanTemplate writtenTemplate, ActionInfo? writtenActions, uint[]? canonicalConstants)
        {
            ArgumentNullException.ThrowIfNull(writtenTemplate);
            ArgumentNullException.ThrowIfNull(template);
            ArgumentNullException.ThrowIfNull(description);
            ArgumentNullException.ThrowIfNull(symbols);
            ArgumentNullException.ThrowIfNull(idConstants);
            ArgumentNullException.ThrowIfNull(extraConstraints);
            if (instance is not null && (ReferenceEquals(instance.Template, template) == false || instance.Action != action))
                throw new ArgumentException("The instance belongs to another template or action.", nameof(instance));

            Template = template;
            Action = action;
            Instance = instance;
            Description = description;
            Symbols = symbols;
            IdConstants = idConstants;
            ExtraConstraints = extraConstraints;
            WrittenTemplate = writtenTemplate;
            WrittenActions = writtenActions;
            CanonicalConstants = canonicalConstants;
        }

        /// <summary>
        /// Gets the plan template.
        /// </summary>
        public PlanTemplate Template { get; }

        /// <summary>
        /// Gets the rule's action id in its rule set; its index is the rule's <see cref="RuleId"/> index.
        /// </summary>
        public ActionId Action { get; }

        /// <summary>
        /// Gets the header subsets evaluated when the rule was added, or <c>null</c> when a header selected no rows.
        /// </summary>
        public PlanInstance? Instance { get; }

        /// <summary>
        /// Gets whether the rule provably matches nothing (a header was empty when it was built).
        /// </summary>
        public bool IsEmpty => Instance is null;

        /// <summary>
        /// Gets the rule's description, which keys its report.
        /// </summary>
        public string Description { get; }

        /// <summary>
        /// Gets the names of the rule's atoms and variables.
        /// </summary>
        public SymbolMap Symbols { get; }

        /// <summary>
        /// Gets the constant ids the rule's atoms match against. Callers must not modify the array.
        /// </summary>
        public IdConstant[] IdConstants { get; }

        /// <summary>
        /// Gets the template as planned, with the Id constants as written: <see cref="Template"/>, unless the rule runs a
        /// variant for K (colored-contexts spec §9.8). <see cref="RuleSet.BuildCachedPlan"/> caches this one, so a cached
        /// plan never bakes one context's canonical forms.
        /// </summary>
        public PlanTemplate WrittenTemplate { get; }

        /// <summary>
        /// Gets the action program as written, or <c>null</c> when the program the rule set runs for the rule is it.
        /// </summary>
        public ActionInfo? WrittenActions { get; }

        /// <summary>
        /// Gets K, the executed form of the rule's Id constants in the context it was added in (query constants first,
        /// then action constants), or <c>null</c> when the rule has none (colored-contexts spec §9.8, I30).
        /// </summary>
        public uint[]? CanonicalConstants { get; }

        /// <summary>
        /// Gets the extra constraints of a rule added from a cached plan, in header order: egglog keeps them as headers
        /// of the rule's plan, so a cached plan built from this rule keeps them too
        /// (<see cref="RuleSet.BuildCachedPlan"/>). Callers must not modify the array.
        /// </summary>
        public (AtomId Atom, Constraint Constraint)[] ExtraConstraints { get; }

        /// <summary>
        /// Formats the description, the action and the plan.
        /// </summary>
        /// <returns>The text.</returns>
        public override string ToString()
        {
            var b = new StringBuilder();
            b.Append('"').Append(Description).Append("\" -> ").Append(Action);
            if (IsEmpty)
                b.Append(" (empty)");

            return b.AppendLine().Append(Template).ToString();
        }

    }

}

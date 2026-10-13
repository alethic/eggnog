using System;
using System.Threading;

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
    /// Holds no row subsets, so it stays valid however the tables change, and it is immutable apart from its cache of
    /// constant variants, which is thread-safe: one instance may be added to any number of rule sets, on any thread, in
    /// any context (the executor's per-context header evaluation and the executed form of the Id constants, K, are what
    /// differ between contexts: <see cref="GetVariantFor"/>).
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
            WrittenConstants = CanonicalConstants.Written(idConstants, actions.IdConstants);
        }

        /// <summary>
        /// The most variants a cached plan keeps. A new variant beyond them replaces the oldest, so the variant of the
        /// most recent K is always cached: a store-backed root has one current K per rule, and a rule whose constant has
        /// changed its canonical form many times keeps paying O(constants) per run, not a rebuild of its variant.
        /// </summary>
        public const int MaxVariants = 16;

        readonly object variantGate = new();
        ConstantVariant[] variants = [];

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

        /// <summary>
        /// Gets the written values of the rule's Id constants, query constants (<see cref="IdConstants"/>) first and then
        /// action constants (<see cref="ActionInfo.IdConstants"/>), or <c>null</c> when it has none.
        /// <see cref="CanonicalConstants.Compute"/> returns this very array when every constant is canonical. Callers
        /// must not modify it.
        /// </summary>
        public uint[]? WrittenConstants { get; }

        /// <summary>
        /// Gets the number of variants cached so far.
        /// </summary>
        internal int VariantCount => Volatile.Read(ref variants).Length;

        /// <summary>
        /// Gets the template and action program to run for the executed constants <paramref name="canonical"/>: the
        /// written ones when K is <c>null</c> or equals <see cref="WrittenConstants"/>; otherwise a
        /// <see cref="ConstantVariant"/> with the changed constants rewritten (<see cref="TemplateSubstituter"/>,
        /// <see cref="ActionSubstituter"/>), cached copy-on-write per K, at most <see cref="MaxVariants"/> of them
        /// (inserted under a lock, then double-checked; when the cache is full the oldest variant makes room for the new
        /// one).
        /// </summary>
        /// <remarks>
        /// Thread-safe: concurrent contexts may ask for variants of one cached plan. Lock-free once a K's variant exists.
        /// </remarks>
        /// <param name="canonical">K, from <see cref="CanonicalConstants.Compute"/>.</param>
        /// <returns>The template and the program.</returns>
        /// <exception cref="ArgumentException">K does not have one value per constant.</exception>
        public (PlanTemplate Plan, ActionInfo Actions) GetVariantFor(uint[]? canonical)
        {
            if (canonical is null || ReferenceEquals(canonical, WrittenConstants) || (WrittenConstants is { } written && written.AsSpan().SequenceEqual(canonical)))
                return (Plan, Actions);

            foreach (var variant in Volatile.Read(ref variants))
                if (variant.Matches(canonical))
                    return (variant.Template, variant.Actions);

            lock (variantGate)
            {
                var current = variants;
                foreach (var variant in current)
                    if (variant.Matches(canonical))
                        return (variant.Template, variant.Actions);

                var substitution = ConstantSubstitution.Create(IdConstants, Actions.IdConstants, canonical);
                if (substitution is null)
                    return (Plan, Actions);

                var made = new ConstantVariant((uint[])canonical.Clone(), TemplateSubstituter.Substitute(Plan, substitution), ActionSubstituter.Substitute(Actions, substitution));
                Volatile.Write(ref variants, current.Length < MaxVariants ? [.. current, made] : [.. current.AsSpan(1), made]);
                return (made.Template, made.Actions);
            }
        }

    }

}

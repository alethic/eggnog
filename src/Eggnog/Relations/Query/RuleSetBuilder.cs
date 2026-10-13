using System;
using System.Diagnostics;

namespace Eggnog.Relations
{

    /// <summary>
    /// Builds a <see cref="RuleSet"/> over a <see cref="Database"/>. egglog's <c>RuleSetBuilder</c>
    /// (core-relations/src/query.rs:82-280); <see cref="Database.NewRuleSet"/> creates one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Rules come in two ways. <see cref="NewRule"/> starts a rule from scratch: declare variables and atoms on the
    /// returned <see cref="RuleQueryBuilder"/>, then the right-hand side on the <see cref="RuleBuilder"/> it builds,
    /// whose <see cref="RuleBuilder.Build(string)"/> plans the query once and adds the rule. Or
    /// <see cref="AddRuleFromCachedPlan"/> adds a rule planned earlier (<see cref="RuleSet.BuildCachedPlan"/>), with extra
    /// constraints such as semi-naive timestamp bounds, re-evaluating only its headers. Either way the rule gets the
    /// next <see cref="RuleId"/> and an action with the same index.
    /// </para>
    /// <para>
    /// At most one rule is under construction at a time, as egglog's borrow of the builder enforces: starting another
    /// rule, adding a cached one or calling <see cref="Build"/> abandons the open rule, whose builders then throw
    /// <see cref="InvalidOperationException"/>. An abandoned rule adds nothing. After <see cref="Build"/> the builder
    /// throws on every call.
    /// </para>
    /// <para>
    /// Constraint processing reads the database (subsets, cached column indexes), so the database must not merge or
    /// rebuild while a rule set is being built. Not thread-safe. Cold path.
    /// </para>
    /// </remarks>
    internal sealed class RuleSetBuilder
    {

        readonly Database db;
        readonly ContextView view;
        readonly IdVec<RuleId, RulePlan> rules = new();
        readonly DenseIdMap<ActionId, ActionInfo> actions = new();
        object? open;
        bool built;

        /// <summary>
        /// Whether a header has been evaluated, so <see cref="pinnedGeneration"/> and <see cref="pinnedStamp"/> are set.
        /// </summary>
        bool pinned;

        /// <summary>
        /// The view's <see cref="ContextView.Generation"/> when the first header was evaluated.
        /// </summary>
        long pinnedGeneration;

        /// <summary>
        /// The view's <see cref="ContextView.Stamp"/> when the first header was evaluated.
        /// </summary>
        long pinnedStamp;

        /// <summary>
        /// Initializes a new instance. egglog's <c>RuleSetBuilder::new</c>.
        /// </summary>
        /// <param name="database">The database the rules run against.</param>
        public RuleSetBuilder(Database database)
        {
            ArgumentNullException.ThrowIfNull(database);
            db = database;
            view = database.View;
        }

        /// <summary>
        /// Gets the database the rules run against.
        /// </summary>
        public Database Database => db;

        /// <summary>
        /// Gets the view rules are instantiated through and the rule set pins (<see cref="Database.View"/>).
        /// </summary>
        public ContextView View => view;

        /// <summary>
        /// Gets the number of rules added so far.
        /// </summary>
        public int Count => rules.Count;

        /// <summary>
        /// Estimates the number of rows of <paramref name="table"/>, or of its rows matching
        /// <paramref name="constraint"/>. egglog's <c>estimate_size</c>, a wrapper around
        /// <see cref="Database.EstimateSize"/>.
        /// </summary>
        /// <param name="table">The table.</param>
        /// <param name="constraint">The constraint, or <c>null</c>.</param>
        /// <returns>The estimate.</returns>
        /// <remarks>
        /// In a child, its view's estimate: the sum over the layers of the table's view.
        /// </remarks>
        public int EstimateSize(TableId table, Constraint? constraint = null) => db.EstimateSize(table, constraint);

        /// <summary>
        /// Starts a rule. egglog's <c>new_rule</c>. Abandons the rule under construction, if any.
        /// </summary>
        /// <returns>The builder of the rule's query.</returns>
        public RuleQueryBuilder NewRule()
        {
            ThrowIfBuilt();
            var rule = new RuleQueryBuilder(this, new QueryBuilder(db));
            open = rule;
            return rule;
        }

        /// <summary>
        /// Adds a rule planned earlier, with <paramref name="extraConstraints"/> evaluated as headers before its own.
        /// egglog's <c>add_rule_from_cached_plan</c> and <c>get_rule_with_extra_constraints</c>
        /// (core-relations/src/query.rs:191-274). Abandons the rule under construction, if any.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Each extra constraint becomes a header of its own, in order; then the cached plan's own extra constraints;
        /// then the template's headers. All are evaluated against the current tables (the template's stages and the
        /// action program are reused as they are). If any header selects no rows, the rule cannot match: nothing is
        /// added, no action id is taken (so rules and actions stay in bijection), and the result is <c>null</c>. That
        /// is how a semi-naive variant whose focus atom has no new rows is skipped.
        /// </para>
        /// <para>
        /// O(headers) constraint evaluations, typically binary searches on a sorted timestamp column; no planning.
        /// </para>
        /// <para>
        /// Id constants: the rule runs K, the canonical form of each of its Id constants in this database's context
        /// (<paramref name="canonical"/>, computed through <see cref="View"/> when not given): the cached plan's variant
        /// for K (<see cref="CachedPlan.GetVariantFor"/>), which is the written plan itself whenever every constant is
        /// canonical, as it always is in a bare database.
        /// </para>
        /// </remarks>
        /// <param name="cached">The cached plan.</param>
        /// <param name="extraConstraints">Constraints on atoms of the plan; each must have a fast evaluation.</param>
        /// <param name="canonical">K, when the caller computed it (<see cref="CanonicalConstants.Compute"/>, the bridge's
        /// watermark check); <c>null</c> to compute it here.</param>
        /// <returns>The new rule, or <c>null</c> when it provably matches nothing.</returns>
        /// <exception cref="ArgumentException">An extra constraint names an atom the plan does not have, or K does not
        /// have one value per constant.</exception>
        /// <exception cref="InvalidOperationException">A constraint has no fast evaluation (egglog panics: cached plans
        /// only support constraints with a fast pushdown).</exception>
        public RuleId? AddRuleFromCachedPlan(CachedPlan cached, ReadOnlySpan<(AtomId Atom, Constraint Constraint)> extraConstraints = default, uint[]? canonical = null)
        {
            ArgumentNullException.ThrowIfNull(cached);
            ThrowIfBuilt();
            open = null;

            var extras = CombineExtras(extraConstraints, cached.ExtraConstraints);
            canonical ??= CanonicalConstants.Compute(view, cached.IdConstants, cached.Actions.IdConstants, cached.WrittenConstants);
            var (template, program) = cached.GetVariantFor(canonical);

            // Peek at the action id without taking it, so an empty variant does not break the bijection.
            var action = actions.NextId;
            PinViews();
            var evaluator = view;
            var instance = template.InstantiateLayered(ref evaluator, extras, action);
            if (instance is null)
                return null;

            var pushed = actions.Push(program);
            Debug.Assert(pushed == action, "the peeked action id must be the one pushed");
            var rule = rules.Push(new RulePlan(template, action, instance, cached.Description, cached.Symbols, cached.IdConstants, extras, cached.Plan, cached.Actions, canonical));
            Debug.Assert(rule.Index == action.Index, "rule and action ids must stand in a bijection");
            return rule;
        }

        /// <summary>
        /// Finishes the rule set. egglog's <c>build</c>. Abandons the rule under construction, if any; the builder
        /// cannot be used afterwards.
        /// </summary>
        /// <returns>The rule set, which the caller disposes.</returns>
        /// <remarks>
        /// <para>
        /// The rule set pins the views of every table its rules' atoms and lookups read
        /// (<see cref="ContextView.Snapshot(ReadOnlySpan{RulePlan}, DenseIdMap{ActionId, ActionInfo}, TableId?)"/>),
        /// and records the view's generation and stamp as they were when its first header was evaluated, so the run's
        /// headers and trie roots read the same views. A merge between the first header and the run, in the context or an
        /// ancestor, moves one of them, and a child's run then refuses the rule set (<see cref="Database.RunRuleSet"/>):
        /// its header subsets name rows of the views as they were when the headers were evaluated, which the run's
        /// snapshot does not show. With neither moved, the snapshot holds the cached view instances header evaluation
        /// read.
        /// </para>
        /// <para>
        /// At a root the views are its single-layer views, shared by every rule set, so building allocates no snapshot.
        /// </para>
        /// </remarks>
        public RuleSet Build()
        {
            ThrowIfBuilt();
            built = true;
            open = null;
            PinViews();
            var skip = db.IsChild ? db.Owner!.Store.UnionFindTable : (TableId?)null;
            var snapshot = view.Snapshot(rules.AsSpan(), actions, skip);
            return new RuleSet(rules, actions, view, snapshot, pinnedGeneration, pinnedStamp);
        }

        /// <summary>
        /// Plans and adds the rule <paramref name="query"/> describes, with its action program: the second half of
        /// egglog's <c>build_with_description</c> (core-relations/src/query.rs:551-576). Called by
        /// <see cref="RuleBuilder.Build(string)"/>.
        /// </summary>
        /// <remarks>
        /// egglog pushes the action before planning; here the plan and the header subsets are computed first and the
        /// rule and its action are pushed together, so a failure leaves the rule set unchanged. A header that selects no
        /// rows still yields a rule, which cannot match (<see cref="RulePlan.IsEmpty"/>), as in egglog.
        /// </remarks>
        /// <param name="builder">The rule's builder, which must be the open one; it is closed.</param>
        /// <param name="query">The rule's query builder.</param>
        /// <param name="instrs">The action program; owned by the rule afterwards.</param>
        /// <param name="idConstants">The constant ids the rule's atoms match against.</param>
        /// <param name="description">The rule's description.</param>
        /// <returns>The new rule.</returns>
        /// <exception cref="InvalidOperationException">The program reads a variable that no atom binds and no
        /// instruction defines (egglog leaves this undefined: the join would push an unbound value; see
        /// core-relations/src/action/mod.rs:225-235).</exception>
        internal RuleId AddBuiltRule(RuleBuilder builder, QueryBuilder query, Instr[] instrs, IdConstant[] idConstants, string description) =>
            AddBuiltRule(builder, query, instrs, idConstants, [], description);

        /// <summary>
        /// Plans and adds the rule <paramref name="query"/> describes, with its action program and the program's constant
        /// ids: <see cref="AddBuiltRule(RuleBuilder, QueryBuilder, Instr[], IdConstant[], string)"/>, after which the rule
        /// runs K: the query is planned with its constants as written, then the template and the program are rewritten to
        /// the constants' canonical forms in this database's context before the headers are evaluated. The rule keeps the
        /// written ones for <see cref="RuleSet.BuildCachedPlan"/>.
        /// </summary>
        /// <param name="builder">The rule's builder, which must be the open one; it is closed.</param>
        /// <param name="query">The rule's query builder.</param>
        /// <param name="instrs">The action program; owned by the rule afterwards.</param>
        /// <param name="idConstants">The constant ids the rule's atoms match against.</param>
        /// <param name="actionIdConstants">The constant ids among the program's operands.</param>
        /// <param name="description">The rule's description.</param>
        /// <returns>The new rule.</returns>
        /// <exception cref="InvalidOperationException">The program reads a variable that no atom binds and no
        /// instruction defines.</exception>
        internal RuleId AddBuiltRule(RuleBuilder builder, QueryBuilder query, Instr[] instrs, IdConstant[] idConstants, ActionIdConstant[] actionIdConstants, string description)
        {
            ThrowIfNotOpen(builder);

            var action = actions.NextId;
            var q = query.Build(action);
            open = null;

            // egglog's build_with_description (core-relations/src/query.rs:555-561) keeps a used variable no atom
            // binds, and Bindings::push (core-relations/src/action/mod.rs:225-235) then reads its unset slot with
            // unwrap_unchecked, which is undefined behaviour; such a program is rejected here instead.
            var used = q.GetUsedVariables();
            foreach (var variable in used)
                if (q.Variables[variable].Occurrences.Count == 0)
                    throw new InvalidOperationException($"The action reads {variable}, which no atom of the query binds.");

            var symbols = SymbolMap.Create(db, q);
            var written = db.PlanQuery(q);
            var writtenActions = new ActionInfo(instrs, used, actionIdConstants);
            var canonical = CanonicalConstants.Compute(view, idConstants, actionIdConstants);
            var template = written;
            var program = writtenActions;
            if (ConstantSubstitution.Create(idConstants, actionIdConstants, canonical) is { } substitution)
            {
                template = TemplateSubstituter.Substitute(written, substitution);
                program = ActionSubstituter.Substitute(writtenActions, substitution);
            }

            PinViews();
            var evaluator = view;
            var instance = template.InstantiateLayered(ref evaluator, default, action);

            var pushed = actions.Push(program);
            Debug.Assert(pushed == action, "the peeked action id must be the one pushed");
            var rule = rules.Push(new RulePlan(template, action, instance, description, symbols, idConstants, Array.Empty<(AtomId, Constraint)>(), written, writtenActions, canonical));
            Debug.Assert(rule.Index == action.Index, "rule and action ids must stand in a bijection");
            return rule;
        }

        /// <summary>
        /// Evaluates the headers of one instantiation of <paramref name="cached"/>. egglog's
        /// <c>get_rule_with_extra_constraints</c> (core-relations/src/query.rs:191-243), on
        /// <see cref="PlanTemplate.InstantiateLayered{TEvaluator}"/> through the database's view: one subset per layer of
        /// each table's view, the root's single layer at a root.
        /// </summary>
        /// <param name="cached">The cached plan.</param>
        /// <param name="action">The action id of the instantiation.</param>
        /// <param name="extraConstraints">All extra constraints, in header order.</param>
        /// <returns>The instance, or <c>null</c> when a header selects no rows.</returns>
        internal PlanInstance? GetRuleWithExtraConstraints(CachedPlan cached, ActionId action, ReadOnlySpan<(AtomId Atom, Constraint Constraint)> extraConstraints)
        {
            PinViews();
            var evaluator = view;
            return cached.Plan.InstantiateLayered(ref evaluator, extraConstraints, action);
        }

        /// <summary>
        /// Throws unless <paramref name="builder"/> is the rule under construction.
        /// </summary>
        /// <param name="builder">A <see cref="RuleQueryBuilder"/> or <see cref="RuleBuilder"/> of this builder.</param>
        internal void ThrowIfNotOpen(object builder)
        {
            if (ReferenceEquals(open, builder) == false)
                throw new InvalidOperationException("The rule is no longer under construction: it was built, or its rule set builder moved on to another rule or was built.");
        }

        /// <summary>
        /// Hands the open rule from its query builder to its rule builder.
        /// </summary>
        /// <param name="query">The open query builder.</param>
        /// <param name="rule">The rule builder taking over.</param>
        internal void HandOver(RuleQueryBuilder query, RuleBuilder rule)
        {
            ThrowIfNotOpen(query);
            open = rule;
        }

        /// <summary>
        /// Concatenates this instantiation's extra constraints and the cached plan's, sharing the cached array when this
        /// instantiation adds none.
        /// </summary>
        static (AtomId Atom, Constraint Constraint)[] CombineExtras(ReadOnlySpan<(AtomId Atom, Constraint Constraint)> extras, (AtomId Atom, Constraint Constraint)[] cached)
        {
            if (extras.IsEmpty)
                return cached;

            var all = new (AtomId Atom, Constraint Constraint)[extras.Length + cached.Length];
            extras.CopyTo(all);
            cached.CopyTo(all.AsSpan(extras.Length));
            return all;
        }

        /// <summary>
        /// Records the view's generation and stamp before the first header is evaluated: what a child's run checks the
        /// views still are, so its headers and trie roots read the same views. O(depth) once per builder; a root's are
        /// constant.
        /// </summary>
        void PinViews()
        {
            if (pinned)
                return;

            pinnedGeneration = view.Generation;
            pinnedStamp = view.Stamp;
            pinned = true;
        }

        /// <summary>
        /// Throws once <see cref="Build"/> has been called, as egglog's <c>build</c> consumes the builder.
        /// </summary>
        void ThrowIfBuilt()
        {
            if (built)
                throw new InvalidOperationException("The rule set has already been built.");
        }

    }

}

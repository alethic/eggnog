using System;
using System.Buffers;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace Eggnog.Relations
{

    /// <summary>
    /// An e-graph rule as recorded by an <see cref="EGraphRuleBuilder"/>: its typed variables, its table atoms (each with
    /// its own timestamp variable), the actions to lower in order, and how semi-naive evaluation treats it.
    /// egglog-bridge's <c>Query</c> (egglog-bridge/src/rule.rs:100-123, 718-847).
    /// </summary>
    /// <remarks>
    /// <para>
    /// One e-graph rule becomes one core rule, planned once (<see cref="BuildCachedPlan(Database, string, ValueTuple{TableId, ExternalFunctionId}?)"/>), which every run instantiates
    /// as several core rules, its semi-naive variants (<see cref="AddRulesFromCached(RuleSetBuilder, uint, CachedPlanInfo, uint[])"/>): each variant constrains the
    /// timestamp columns of some atoms, so that together they visit exactly the matches that involve at least one row
    /// new since the rule's last run.
    /// </para>
    /// <para>
    /// Left-hand-side primitives are not join atoms: they are actions (<see cref="RuleActionKind.QueryPrim"/>), filters
    /// evaluated after the join on every match of every variant, and take no part in the variants.
    /// </para>
    /// <para>
    /// Mutable while its builder records it, then immutable: a built rule's query may be planned and instantiated from
    /// any number of threads. Cold path, except <see cref="AddRulesFromCached(RuleSetBuilder, uint, CachedPlanInfo, uint[])"/>, which runs once per rule per run and
    /// allocates only what the core rule set keeps.
    /// </para>
    /// </remarks>
    internal sealed class BridgeQuery
    {

        readonly List<RuleVarInfo> vars = new();
        readonly List<RuleAtom> atoms = new();
        readonly List<RuleAction> actions = new();
        readonly List<ExternalFunctionId> ownedFunctions = new();

        /// <summary>
        /// Initializes an empty rule. egglog's <c>new_rule</c> (egglog-bridge/src/rule.rs:134-156).
        /// </summary>
        /// <param name="unionFind">The e-graph's union-find table.</param>
        /// <param name="idCounter">The counter fresh ids come from.</param>
        /// <param name="timestampCounter">The timestamp counter, read by the first instruction of the lowered rule.</param>
        /// <param name="ruleId">The rule's id in its e-graph.</param>
        /// <param name="semiNaive">Whether runs consider only matches involving new rows.</param>
        public BridgeQuery(TableId unionFind, CounterId idCounter, CounterId timestampCounter, EGraphRuleId ruleId, bool semiNaive)
        {
            UnionFind = unionFind;
            IdCounter = idCounter;
            TimestampCounter = timestampCounter;
            RuleId = ruleId;
            SemiNaive = semiNaive;
            SoleFocus = -1;
        }

        /// <summary>
        /// Gets the e-graph's union-find table.
        /// </summary>
        public TableId UnionFind { get; }

        /// <summary>
        /// Gets the counter fresh ids come from.
        /// </summary>
        public CounterId IdCounter { get; }

        /// <summary>
        /// Gets the timestamp counter.
        /// </summary>
        public CounterId TimestampCounter { get; }

        /// <summary>
        /// Gets the rule's id in its e-graph.
        /// </summary>
        public EGraphRuleId RuleId { get; }

        /// <summary>
        /// Gets whether runs consider only matches involving rows new since the rule's last run; otherwise every run
        /// considers every match (egglog's <c>:naive</c> rules and one-shot commands).
        /// </summary>
        public bool SemiNaive { get; }

        /// <summary>
        /// Gets or sets the stage-ordering strategy of the plan. egglog's <c>plan_strategy</c>, by default
        /// <see cref="PlanStrategy.Gj"/>; a rule with one atom is planned with <see cref="PlanStrategy.MinCover"/>.
        /// </summary>
        public PlanStrategy Strategy { get; set; }

        /// <summary>
        /// Gets or sets whether the planner skips tree decomposition. egglog's <c>no_decomp</c>.
        /// </summary>
        public bool NoDecomposition { get; set; }

        /// <summary>
        /// Gets or sets the index of the only atom semi-naive runs constrain to new rows, or -1 for the usual variants.
        /// egglog's <c>sole_focus</c>: one variant per run instead of one per atom, which is complete only when every
        /// match a run must see involves a new row of that atom (the bridge's incremental rebuild rules, whose focus is
        /// the union-find atom).
        /// </summary>
        public int SoleFocus { get; set; }

        /// <summary>
        /// Gets the variables, indexed by <see cref="VariableId"/>.
        /// </summary>
        public IReadOnlyList<RuleVarInfo> Variables => vars;

        /// <summary>
        /// Gets the table atoms, indexed by <see cref="RuleAtomId"/>, in the order of the semi-naive variants.
        /// </summary>
        public IReadOnlyList<RuleAtom> Atoms => atoms;

        /// <summary>
        /// Gets the actions, in recording order: the order they are lowered and run in.
        /// </summary>
        public IReadOnlyList<RuleAction> Actions => actions;

        /// <summary>
        /// Gets the external functions created for this rule alone (its lazy panics), freed with the rule.
        /// </summary>
        public ReadOnlySpan<ExternalFunctionId> OwnedFunctions => CollectionsMarshal.AsSpan(ownedFunctions);

        /// <summary>
        /// Adds a variable.
        /// </summary>
        /// <param name="info">Its type and name.</param>
        /// <returns>Its id, the next in order.</returns>
        public VariableId AddVariable(RuleVarInfo info)
        {
            vars.Add(info);
            return new VariableId(vars.Count - 1);
        }

        /// <summary>
        /// Adds a table atom.
        /// </summary>
        /// <param name="atom">The atom.</param>
        /// <returns>Its id, the next in order.</returns>
        public RuleAtomId AddAtom(RuleAtom atom)
        {
            ArgumentNullException.ThrowIfNull(atom);
            atoms.Add(atom);
            return new RuleAtomId(atoms.Count - 1);
        }

        /// <summary>
        /// Appends an action.
        /// </summary>
        /// <param name="action">The action.</param>
        public void AddAction(RuleAction action)
        {
            ArgumentNullException.ThrowIfNull(action);
            actions.Add(action);
        }

        /// <summary>
        /// Records an external function created for this rule alone, to free with it.
        /// </summary>
        /// <param name="function">The function.</param>
        public void AddOwnedFunction(ExternalFunctionId function) => ownedFunctions.Add(function);

        /// <summary>
        /// Checks that every action reads only variables bound when it runs, and that the sole focus, if any, names an
        /// atom. Called when the rule is built.
        /// </summary>
        /// <exception cref="InvalidOperationException">An action reads a variable that no atom binds and no earlier action
        /// defines, or the sole focus names no atom.</exception>
        public void Validate()
        {
            if (SoleFocus >= atoms.Count)
                throw new InvalidOperationException($"The sole focus is atom {SoleFocus}, but the rule has {atoms.Count} atoms.");

            var bound = new FixedBitSet(Math.Max(vars.Count, 1));
            var grounded = new FixedBitSet(Math.Max(vars.Count, 1));
            foreach (var atom in atoms)
            {
                foreach (var entry in atom.Entries)
                {
                    if (entry.IsVariable)
                    {
                        bound.Insert(entry.Variable.Id.Index);
                        grounded.Insert(entry.Variable.Id.Index);
                    }
                }
            }

            for (var i = 0; i < actions.Count; i++)
                actions[i].Validate(bound, grounded, i);
        }

        /// <summary>
        /// Lowers the rule onto a core rule and plans it with the current table statistics. egglog's
        /// <c>build_cached_plan</c> (egglog-bridge/src/rule.rs:719-771).
        /// </summary>
        /// <remarks>
        /// One core variable per rule variable, named alike; each atom over the same table with its entries translated
        /// (constants become equality constraints in the core); then the right-hand side: first a read of the timestamp
        /// counter, the variable every write is stamped with, then every action in order. The core plans the query once;
        /// the plan is never revisited. O(rule size) plus planning.
        /// </remarks>
        /// <param name="db">The database.</param>
        /// <param name="description">The rule's description.</param>
        /// <returns>The plan.</returns>
        /// <exception cref="QueryException">The core rejects the rule.</exception>
        public CachedPlanInfo BuildCachedPlan(Database db, string description) => BuildCachedPlan(db, description, null);

        /// <summary>
        /// <see cref="BuildCachedPlan(Database, string)"/>, with the contradiction early stop: when
        /// <paramref name="stopOnContradiction"/> is given, every head write into its table (the e-graph's contradiction
        /// function) is followed by a call to its function (<see cref="StopOnContradictionFunction"/>).
        /// </summary>
        /// <remarks>
        /// Constants of table columns are e-class ids exactly where the column is one, which the core reads from the
        /// table; constants without a column (external arguments, comparisons) written as ids are lowered through
        /// <see cref="QueryEntry.IdConst"/> (<see cref="RuleBindings.ConvertLoose"/>). A store-backed context runs
        /// every one of them in its canonical form.
        /// </remarks>
        /// <param name="db">The database.</param>
        /// <param name="description">The rule's description.</param>
        /// <param name="stopOnContradiction">The contradiction function's table and the early-stop function, or
        /// <c>null</c>.</param>
        /// <returns>The plan.</returns>
        /// <exception cref="QueryException">The core rejects the rule.</exception>
        public CachedPlanInfo BuildCachedPlan(Database db, string description, (TableId Table, ExternalFunctionId Stop)? stopOnContradiction)
        {
            ArgumentNullException.ThrowIfNull(db);
            ArgumentNullException.ThrowIfNull(description);

            var rsb = new RuleSetBuilder(db);
            var query = rsb.NewRule();
            query.Strategy = Strategy;
            query.NoDecomposition = NoDecomposition;

            // egglog's query_state: a core variable for every rule variable, whether an atom or an action binds it.
            var bindings = new RuleBindings(vars.Count);
            for (var i = 0; i < vars.Count; i++)
                bindings.Bind(new VariableId(i), query.NewVariable(vars[i].Name));

            if (stopOnContradiction is { } stop)
                bindings.SetStopOnContradiction(stop.Table, stop.Stop);

            var atomMapping = new AtomId[atoms.Count];
            for (var i = 0; i < atoms.Count; i++)
            {
                var atom = atoms[i];
                foreach (var entry in atom.Entries)
                    if (entry.IsVariable)
                        bindings.Ground(entry.Variable.Id);

                atomMapping[i] = query.AddAtom(atom.Table, bindings.ConvertAll(atom.Entries));
            }

            var rule = query.Build();
            bindings.SetNextTimestamp(rule.ReadCounter(TimestampCounter));
            foreach (var action in actions)
                action.Lower(bindings, rule);

            var coreRule = rule.Build(description);
            using var ruleSet = rsb.Build();
            return new CachedPlanInfo(ruleSet.BuildCachedPlan(coreRule), atomMapping);
        }

        /// <summary>
        /// Adds this run's instantiations of the rule to <paramref name="ruleSet"/>: its semi-naive variants, or the plan
        /// alone. egglog's <c>add_rules_from_cached</c> (egglog-bridge/src/rule.rs:773-846).
        /// </summary>
        /// <remarks>
        /// <para>
        /// With <paramref name="lastRunAt"/> the rule's watermark <c>m</c>:
        /// </para>
        /// <list type="bullet">
        /// <item>A rule that is not semi-naive, or a rule without atoms that has never run (<c>m = 0</c>), is added once
        /// without constraints. A semi-naive rule without atoms therefore fires exactly once in its life.</item>
        /// <item>A rule with a sole focus <c>f</c> is added once, with atom <c>f</c> constrained to <c>ts &gt;= m</c>.</item>
        /// <item>Otherwise, for each atom <c>i</c>, a variant with atom <c>i</c> constrained to <c>ts &gt;= m</c> (new)
        /// and every earlier atom to <c>ts &lt; m</c> (old); later atoms are unconstrained. For atoms <c>A B C</c> that is
        /// <c>A_new x B x C + A_old x B_new x C + A_old x B_old x C_new</c>, which partitions the matches with at least
        /// one new row. On a first run (<c>m = 0</c>) nothing is old, so only the first variant is added.</item>
        /// </list>
        /// <para>
        /// The focus constraint comes first, so a variant whose focus atom has no new rows is dropped after one binary
        /// search on the sorted timestamp column (<see cref="RuleSetBuilder.AddRuleFromCachedPlan"/> returns
        /// <c>null</c>). O(atoms²) constraint evaluations, each a binary search.
        /// </para>
        /// </remarks>
        /// <param name="ruleSet">The rule set being built for the run.</param>
        /// <param name="lastRunAt">The rule's watermark: the timestamp of its last run, zero if it never ran.</param>
        /// <param name="cached">The rule's plan.</param>
        public void AddRulesFromCached(RuleSetBuilder ruleSet, uint lastRunAt, CachedPlanInfo cached) => AddRulesFromCached(ruleSet, lastRunAt, cached, null);

        /// <summary>
        /// <see cref="AddRulesFromCached(RuleSetBuilder, uint, CachedPlanInfo)"/> with the rule's executed Id constants
        /// <paramref name="canonical"/> (K) computed once for every variant; <c>null</c>
        /// lets the builder compute them per variant (<see cref="RuleSetBuilder.AddRuleFromCachedPlan"/>).
        /// </summary>
        /// <param name="ruleSet">The rule set being built for the run.</param>
        /// <param name="lastRunAt">The rule's watermark: the timestamp of its last run, zero if it never ran.</param>
        /// <param name="cached">The rule's plan.</param>
        /// <param name="canonical">K, from <see cref="CanonicalConstants.Compute"/> on the running context's view, or
        /// <c>null</c>.</param>
        public void AddRulesFromCached(RuleSetBuilder ruleSet, uint lastRunAt, CachedPlanInfo cached, uint[]? canonical)
        {
            ArgumentNullException.ThrowIfNull(ruleSet);
            ArgumentNullException.ThrowIfNull(cached);

            if (SemiNaive == false || (atoms.Count == 0 && lastRunAt == 0))
            {
                ruleSet.AddRuleFromCachedPlan(cached.Plan, default, canonical);
                return;
            }

            var mid = new Value(lastRunAt);
            var mapping = cached.AtomMapping;
            if (SoleFocus >= 0)
            {
                // A single focus atom constrained to new rows.
                var focus = (mapping[SoleFocus], Constraint.GeConst(new ColumnId(atoms[SoleFocus].Math.TimestampColumn), mid));
                ruleSet.AddRuleFromCachedPlan(cached.Plan, new ReadOnlySpan<(AtomId, Constraint)>(in focus), canonical);
                return;
            }

            var buffer = ArrayPool<(AtomId Atom, Constraint Constraint)>.Shared.Rent(Math.Max(atoms.Count, 1));
            for (var focus = 0; focus < atoms.Count; focus++)
            {
                // On a first run every row is new and no row is old, so every variant but the first is empty.
                if (focus > 0 && lastRunAt == 0)
                    continue;

                // The focus atom first: if it has no new rows the variant is dropped before the others are evaluated.
                buffer[0] = (mapping[focus], Constraint.GeConst(new ColumnId(atoms[focus].Math.TimestampColumn), mid));
                for (var i = 0; i < focus; i++)
                    buffer[i + 1] = (mapping[i], Constraint.LtConst(new ColumnId(atoms[i].Math.TimestampColumn), mid));

                ruleSet.AddRuleFromCachedPlan(cached.Plan, buffer.AsSpan(0, focus + 1), canonical);
            }

            ArrayPool<(AtomId Atom, Constraint Constraint)>.Shared.Return(buffer);
        }

    }

}

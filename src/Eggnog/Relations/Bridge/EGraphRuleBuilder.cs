using System;

namespace Eggnog.Relations
{

    /// <summary>
    /// Records a rule of an <see cref="EGraph"/>: typed variables, table atoms (the left-hand side), and actions (the
    /// right-hand side), then registers it with <see cref="Build"/>. egglog-bridge's <c>RuleBuilder</c>
    /// (egglog-bridge/src/rule.rs:125-716), from <see cref="EGraph.NewRule"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Atoms are function rows matched by the join: <see cref="QueryTable"/> adds one over a function's table with the
    /// given entries for its columns, followed by a fresh timestamp variable (semi-naive evaluation constrains it) and,
    /// for a function that can subsume, its subsumption cell. Everything else is an action, run in recording order on
    /// each match: left-hand-side primitives (<see cref="QueryPrim"/>, filters evaluated after the join), lookups that
    /// mint terms (<see cref="Lookup"/>), writes (<see cref="Set"/>, <see cref="Union"/>, <see cref="Remove"/>,
    /// <see cref="Subsume"/>), external calls (<see cref="CallExternalFunc"/>) and panics (<see cref="Panic"/>). Every
    /// row a run writes is stamped with the run's timestamp. Nothing is lowered or planned until the rule first runs
    /// (map §1.2 A-B).
    /// </para>
    /// <para>
    /// Primitives and capability contexts (decision 12, as egglog does): external functions receive the raw
    /// <see cref="ExecutionState"/>, and the engine enforces no capability. egglog's frontend registers a primitive once
    /// per context it is valid in (pure, read, write, full) and picks the id per call site: for a semi-naive rule, pure on
    /// the left-hand side and write on the right; for a naive rule, read and full; write in merge functions (map §3.7).
    /// What this builder guarantees is the evaluation discipline those contexts rely on: a left-hand-side primitive
    /// (<see cref="QueryPrim"/>) runs after the join on every match of every variant, and a result of nothing drops the
    /// match silently; a right-hand-side call (<see cref="CallExternalFunc"/>) that produces nothing panics; writes are
    /// staged and become visible only after the run.
    /// </para>
    /// <para>
    /// Every method checks its input before recording anything, so a failing call leaves the rule unchanged. egglog
    /// reports arity errors of lookups and writes only when the rule is first planned; here they are reported when they
    /// are recorded. Not thread-safe; usable until <see cref="Build"/>, after which every member throws
    /// <see cref="InvalidOperationException"/>. Cold path.
    /// </para>
    /// </remarks>
    internal sealed class EGraphRuleBuilder
    {

        readonly EGraph egraph;
        readonly string description;
        readonly BridgeQuery query;
        readonly int generation;
        bool built;

        /// <summary>
        /// Initializes a new instance. Called by <see cref="EGraph.NewRule"/>, which has reserved the rule's id.
        /// </summary>
        /// <param name="egraph">The e-graph.</param>
        /// <param name="description">The rule's description.</param>
        /// <param name="query">The empty rule.</param>
        /// <param name="generation">The generation of the rule's reserved id (colored-contexts spec §10.2).</param>
        internal EGraphRuleBuilder(EGraph egraph, string description, BridgeQuery query, int generation)
        {
            ArgumentNullException.ThrowIfNull(egraph);
            ArgumentNullException.ThrowIfNull(description);
            ArgumentNullException.ThrowIfNull(query);
            this.egraph = egraph;
            this.description = description;
            this.query = query;
            this.generation = generation;
        }

        /// <summary>
        /// Gets the e-graph the rule belongs to. egglog's <c>egraph</c>.
        /// </summary>
        public EGraph EGraph => egraph;

        /// <summary>
        /// Gets the rule's description, which keys its reports.
        /// </summary>
        public string Description => description;

        /// <summary>
        /// Gets the id the rule will have; reserved when the builder was created.
        /// </summary>
        public EGraphRuleId RuleId => query.RuleId;

        /// <summary>
        /// Gets or sets whether the planner skips tree decomposition for this rule. egglog's <c>set_no_decomp</c>, set
        /// from the <c>:no-decomp</c> rule option or the <c>--no-decomp</c> flag.
        /// </summary>
        public bool NoDecomposition
        {
            get => query.NoDecomposition;
            set
            {
                ThrowIfBuilt();
                query.NoDecomposition = value;
            }
        }

        /// <summary>
        /// Gets or sets the stage-ordering strategy of the plan. egglog's <c>set_plan_strategy</c>. <see cref="Build"/>
        /// forces <see cref="PlanStrategy.MinCover"/> on a rule with one atom.
        /// </summary>
        internal PlanStrategy PlanStrategy
        {
            get => query.Strategy;
            set
            {
                ThrowIfBuilt();
                query.Strategy = value;
            }
        }

        /// <summary>
        /// Gets the rule recorded so far.
        /// </summary>
        internal BridgeQuery Query => query;

        /// <summary>
        /// Gets the external function that panics with <paramref name="message"/>: <see cref="EGraph.NewPanic"/>. egglog's
        /// <c>new_panic</c>. Called through <see cref="CallExternalFunc"/>, the panic records the message and stops the
        /// run early, which then throws <see cref="PanicException"/>.
        /// </summary>
        /// <param name="message">The message.</param>
        /// <returns>The function's id.</returns>
        public ExternalFunctionId NewPanic(string message)
        {
            ThrowIfBuilt();
            return egraph.NewPanic(message);
        }

        /// <summary>
        /// Creates a variable of type <paramref name="type"/>. egglog's <c>new_var</c>.
        /// </summary>
        /// <param name="type">The column type the variable holds.</param>
        /// <returns>The variable.</returns>
        public RuleVariable NewVar(ColumnTy type)
        {
            ThrowIfBuilt();
            return new RuleVariable(query.AddVariable(new RuleVarInfo(type, null)), null);
        }

        /// <summary>
        /// Creates a variable of type <paramref name="type"/> named <paramref name="name"/>, which shows in plans and
        /// diagnostics. egglog's <c>new_var_named</c>.
        /// </summary>
        /// <param name="type">The column type the variable holds.</param>
        /// <param name="name">The name.</param>
        /// <returns>The variable.</returns>
        public RuleVariable NewVarNamed(ColumnTy type, string name)
        {
            ArgumentNullException.ThrowIfNull(name);
            ThrowIfBuilt();
            return new RuleVariable(query.AddVariable(new RuleVarInfo(type, name)), name);
        }

        /// <summary>
        /// Adds an atom over the table of <paramref name="function"/>: <paramref name="entries"/> match its columns, keys
        /// then output. egglog's <c>query_table</c> (egglog-bridge/src/rule.rs:407-444).
        /// </summary>
        /// <remarks>
        /// The atom also gets a fresh timestamp variable and, when the function can subsume, an entry for the
        /// subsumption cell: the constant for <paramref name="isSubsumed"/> (<c>false</c>, which the frontend passes
        /// unless the rule asks for subsumed rows, matches only rows that are not subsumed), or a fresh variable when it is
        /// <c>null</c>, which matches either.
        /// </remarks>
        /// <param name="function">The function.</param>
        /// <param name="entries">One entry per column of the function; variables must have the column's type.</param>
        /// <param name="isSubsumed">Which rows to match by subsumption, or <c>null</c> for all.</param>
        /// <returns>The atom's id.</returns>
        /// <exception cref="ArgumentException">The function is not declared, the entry count is not its column count, a
        /// variable's type differs from its column's, or a variable belongs to another rule.</exception>
        public RuleAtomId QueryTable(FunctionId function, ReadOnlySpan<RuleEntry> entries, bool? isSubsumed)
        {
            ThrowIfBuilt();
            var info = egraph.GetFunctionInfo(function);
            var schema = info.Schema;
            if (schema.Length != entries.Length)
                throw new ArgumentException($"query_table: mismatch between [{string.Join(", ", entries.ToArray())}] and the schema of {info.Name}: arity mismatch: expected {schema.Length}, got {entries.Length}.", nameof(entries));

            for (var i = 0; i < entries.Length; i++)
                AssertHasType(entries[i], schema[i], "query_table", nameof(entries));

            RuleEntry? subsume = isSubsumed is bool subsumed ? RuleEntry.Const(new Value(subsumed ? SchemaMath.Subsumed : SchemaMath.NotSubsumed), ColumnTy.Id) : null;
            return AddAtomWithTimestampAndFunc(info.Table, function, subsume, entries);
        }

        /// <summary>
        /// Adds a primitive to the left-hand side: <paramref name="function"/> applied to every entry but the last, whose
        /// result is the last entry. egglog's <c>query_prim</c> (egglog-bridge/src/rule.rs:446-470).
        /// </summary>
        /// <remarks>
        /// Not a join atom: the call runs after the join, on every match of every semi-naive variant, in recording order
        /// with the other actions. If the last entry is a variable that no atom and no earlier primitive bound, it is bound
        /// to the result; otherwise the match is kept only where the result equals it. A match where the function
        /// produces nothing is dropped silently. The frontend passes a primitive registered for the pure context in a
        /// semi-naive rule and for the read context in a naive one (decision 12).
        /// </remarks>
        /// <param name="function">The primitive.</param>
        /// <param name="entries">The arguments followed by the result.</param>
        /// <param name="returnType">The result's type (unused, as in egglog).</param>
        /// <exception cref="ArgumentException">There are no entries, or a variable belongs to another rule.</exception>
        public void QueryPrim(ExternalFunctionId function, ReadOnlySpan<RuleEntry> entries, ColumnTy returnType)
        {
            ThrowIfBuilt();
            if (entries.IsEmpty)
                throw new ArgumentException("query_prim: must specify a return value.", nameof(entries));

            CheckEntries(entries, nameof(entries));
            query.AddAction(RuleAction.QueryPrim(function, entries.ToArray()));
        }

        /// <summary>
        /// Calls <paramref name="function"/> on <paramref name="args"/> and binds the result to a new variable of type
        /// <paramref name="returnType"/>; where the function produces nothing, the rule panics with the message
        /// <paramref name="panicMessage"/> builds. egglog's <c>call_external_func</c>
        /// (egglog-bridge/src/rule.rs:387-405).
        /// </summary>
        /// <remarks>
        /// The panic is a lazy panic function created for this call (<see cref="EGraph.NewPanicLazy"/>), so the message is
        /// built only if the call fails; it belongs to the rule and is freed with it. The frontend passes a primitive
        /// registered for the write context in a semi-naive rule and for the full context in a naive one (decision 12).
        /// </remarks>
        /// <param name="function">The external function.</param>
        /// <param name="args">The arguments.</param>
        /// <param name="returnType">The result's type.</param>
        /// <param name="panicMessage">Builds the panic message.</param>
        /// <returns>The variable holding the result.</returns>
        /// <exception cref="ArgumentException">A variable belongs to another rule.</exception>
        public RuleVariable CallExternalFunc(ExternalFunctionId function, ReadOnlySpan<RuleEntry> args, ColumnTy returnType, Func<string> panicMessage)
        {
            ArgumentNullException.ThrowIfNull(panicMessage);
            ThrowIfBuilt();
            CheckEntries(args, nameof(args));
            var result = NewVar(returnType);
            var panic = egraph.NewPanicLazy(panicMessage);
            query.AddOwnedFunction(panic);
            query.AddAction(RuleAction.CallExternal(function, args.ToArray(), panic, result.Id));
            return result;
        }

        /// <summary>
        /// Looks up the output of <paramref name="function"/> for <paramref name="keys"/> and binds it to a new variable;
        /// a missing key gets the function's default. egglog's <c>lookup</c> (egglog-bridge/src/rule.rs:603-623).
        /// </summary>
        /// <remarks>
        /// For a constructor (<see cref="DefaultValKind.FreshId"/>) a missing key gets a row with a fresh id, which is how
        /// rules create terms; for <see cref="DefaultValKind.Const"/> a row with the constant; for
        /// <see cref="DefaultValKind.Fail"/> the rule panics with the message <paramref name="panicMessage"/> builds.
        /// Within one run, one execution state mints one id per missing key; the run's writes merge afterwards, and the
        /// function's union-id merge reconciles ids minted for the same key elsewhere (map §1.2 E). Inserted rows are not
        /// subsumed.
        /// </remarks>
        /// <param name="function">The function.</param>
        /// <param name="keys">The key, one entry per key column.</param>
        /// <param name="panicMessage">Builds the panic message for a failing default.</param>
        /// <returns>The variable holding the output, of the function's output type.</returns>
        /// <exception cref="ArgumentException">The function is not declared, the key count is not its key count, or a
        /// variable belongs to another rule.</exception>
        public RuleVariable Lookup(FunctionId function, ReadOnlySpan<RuleEntry> keys, Func<string> panicMessage)
        {
            return LookupWithSubsumed(function, keys, RuleEntry.Const(new Value(SchemaMath.NotSubsumed), ColumnTy.Id), panicMessage);
        }

        /// <summary>
        /// Writes the row <paramref name="entries"/> (keys then output) into <paramref name="function"/>, not subsumed;
        /// an existing output for the key is merged with the function's merge. egglog's <c>set</c>
        /// (egglog-bridge/src/rule.rs:655-665).
        /// </summary>
        /// <param name="function">The function.</param>
        /// <param name="entries">One entry per column of the function.</param>
        /// <exception cref="ArgumentException">The function is not declared, the entry count is not its column count, or
        /// a variable belongs to another rule.</exception>
        public void Set(FunctionId function, ReadOnlySpan<RuleEntry> entries)
        {
            SetWithSubsume(function, entries, RuleEntry.Const(new Value(SchemaMath.NotSubsumed), ColumnTy.Id));
        }

        /// <summary>
        /// Removes the row of <paramref name="keys"/> from <paramref name="function"/>, if there is one. egglog's
        /// <c>remove</c> (egglog-bridge/src/rule.rs:694-703).
        /// </summary>
        /// <param name="function">The function.</param>
        /// <param name="keys">The key, one entry per key column.</param>
        /// <exception cref="ArgumentException">The function is not declared, the key count is not its key count, or a
        /// variable belongs to another rule.</exception>
        public void Remove(FunctionId function, ReadOnlySpan<RuleEntry> keys)
        {
            ThrowIfBuilt();
            var info = egraph.GetFunctionInfo(function);
            CheckKeyCount(info, keys, "remove");
            CheckEntries(keys, nameof(keys));
            query.AddAction(RuleAction.Remove(info.Table, keys.ToArray()));
        }

        /// <summary>
        /// Unions two ids in the union-find. egglog's <c>union</c> (egglog-bridge/src/rule.rs:625-633): a row
        /// <c>[left, right, ts]</c> staged into the union-find table; the run's rebuild then canonicalizes the tables.
        /// </summary>
        /// <param name="left">One id.</param>
        /// <param name="right">The other id.</param>
        /// <exception cref="ArgumentException">A variable belongs to another rule.</exception>
        public void Union(RuleEntry left, RuleEntry right)
        {
            ThrowIfBuilt();
            CheckEntry(left, nameof(left));
            CheckEntry(right, nameof(right));
            query.AddAction(RuleAction.Union(query.UnionFind, left, right));
        }

        /// <summary>
        /// Subsumes the row of <paramref name="keys"/> in <paramref name="function"/>: marks it subsumed, inserting it
        /// subsumed (with the function's default output) if it is missing. egglog's <c>subsume</c>
        /// (egglog-bridge/src/rule.rs:472-522).
        /// </summary>
        /// <remarks>
        /// A lookup that inserts a subsumed row for a missing key, then a read of the key's subsumption cell, then an
        /// insertion of the subsumed row where the cell is not yet subsumed; subsumption is sticky, as merges keep the
        /// larger cell. Unlike egglog, a key the table lacks does not halt the rest of the rule's actions (map §8.1 row
        /// 19; see <see cref="RuleAction"/>).
        /// </remarks>
        /// <param name="function">The function; it must be able to subsume.</param>
        /// <param name="keys">The key, one entry per key column.</param>
        /// <exception cref="ArgumentException">The function is not declared or cannot subsume, the key count is not its
        /// key count, or a variable belongs to another rule.</exception>
        public void Subsume(FunctionId function, ReadOnlySpan<RuleEntry> keys)
        {
            ThrowIfBuilt();
            var info = egraph.GetFunctionInfo(function);
            if (info.CanSubsume == false)
                throw new ArgumentException($"subsume: {info.Name} cannot subsume.", nameof(function));

            CheckKeyCount(info, keys, "subsume");
            CheckEntries(keys, nameof(keys));

            // First insert a subsumed row if the key is new, then subsume the row unless it already is.
            var output = LookupWithSubsumed(function, keys, RuleEntry.Const(new Value(SchemaMath.Subsumed), ColumnTy.Id), static () => "subsumed a nonextestent row!");
            query.AddAction(RuleAction.Subsume(info.Table, info.Math, keys.ToArray(), output));
        }

        /// <summary>
        /// Panics with <paramref name="message"/>: records the message and stops the run early, which then throws
        /// <see cref="PanicException"/>. egglog's <c>panic</c> (egglog-bridge/src/rule.rs:705-715). The panic function is
        /// interned by message (<see cref="EGraph.NewPanic"/>).
        /// </summary>
        /// <param name="message">The message.</param>
        public void Panic(string message)
        {
            ArgumentNullException.ThrowIfNull(message);
            ThrowIfBuilt();
            var panic = egraph.NewPanic(message);
            var result = NewVar(ColumnTy.Id);
            query.AddAction(RuleAction.Panic(panic, result.Id));
        }

        /// <summary>
        /// Registers the rule with the e-graph and returns its id. egglog's <c>build</c>
        /// (egglog-bridge/src/rule.rs:293-308). The rule has not run (its watermark is zero) and is planned at its first
        /// run; a rule with exactly one atom is planned with <see cref="PlanStrategy.MinCover"/>.
        /// </summary>
        /// <returns>The rule's id.</returns>
        /// <exception cref="InvalidOperationException">An action reads a variable that no atom binds and no earlier
        /// action defines (egglog leaves this undefined), or the rule was already built.</exception>
        public EGraphRuleId Build()
        {
            ThrowIfBuilt();
            query.Validate();
            if (query.Atoms.Count == 1)
                query.Strategy = PlanStrategy.MinCover;

            built = true;
            egraph.InsertRule(query, description, generation);
            return query.RuleId;
        }

        /// <summary>
        /// Gets the canonical id of <paramref name="entry"/> in the union-find, bound to a new id variable: the entry
        /// itself when it was never displaced. egglog's <c>lookup_uf</c> (egglog-bridge/src/rule.rs:197-214), used by
        /// rule-based rebuilding.
        /// </summary>
        /// <param name="entry">The id.</param>
        /// <returns>The variable holding the canonical id.</returns>
        /// <exception cref="ArgumentException">The entry is a variable that is not of the id type, or of another
        /// rule.</exception>
        internal RuleVariable LookupUf(RuleEntry entry)
        {
            ThrowIfBuilt();
            CheckEntry(entry, nameof(entry));
            AssertHasType(entry, ColumnTy.Id, "lookup_uf", nameof(entry));
            var result = NewVar(ColumnTy.Id);
            query.AddAction(RuleAction.LookupUf(query.UnionFind, entry, result.Id));
            return result;
        }

        /// <summary>
        /// Continues only where <paramref name="left"/> and <paramref name="right"/> differ in some position. egglog's
        /// <c>check_for_update</c> (egglog-bridge/src/rule.rs:216-247), used by rule-based rebuilding.
        /// </summary>
        /// <param name="left">The left entries.</param>
        /// <param name="right">The right entries.</param>
        /// <exception cref="ArgumentException">The lists differ in length, two variables in the same position differ in
        /// type, or a variable belongs to another rule.</exception>
        internal void CheckForUpdate(ReadOnlySpan<RuleEntry> left, ReadOnlySpan<RuleEntry> right)
        {
            ThrowIfBuilt();
            if (left.Length != right.Length)
                throw new ArgumentException($"check_for_update: arity mismatch: expected {left.Length}, got {right.Length}.", nameof(right));

            CheckEntries(left, nameof(left));
            CheckEntries(right, nameof(right));
            for (var i = 0; i < left.Length; i++)
            {
                // Constants are untyped.
                if (left[i].IsVariable && right[i].IsVariable)
                {
                    var leftType = query.Variables[left[i].Variable.Id.Index].Type;
                    var rightType = query.Variables[right[i].Variable.Id.Index].Type;
                    if (leftType != rightType)
                        throw new ArgumentException($"check_for_update: mismatch between {left[i]} and {right[i]}: type mismatch: expected {leftType}, got {rightType}.", nameof(right));
                }
            }

            query.AddAction(RuleAction.AssertAnyNe(left.ToArray(), right.ToArray()));
        }

        /// <summary>
        /// Replaces the row <paramref name="before"/> by <paramref name="after"/>: <see cref="Remove"/> of the old key,
        /// then <see cref="Set"/> of the new row, carrying <paramref name="subsume"/> over as its subsumption cell when
        /// given. egglog's <c>rebuild_row</c> (egglog-bridge/src/rule.rs:635-653), used by rule-based rebuilding.
        /// </summary>
        /// <param name="function">The function.</param>
        /// <param name="before">The old row, keys then output.</param>
        /// <param name="after">The new row, keys then output.</param>
        /// <param name="subsume">The variable holding the old row's subsumption cell, or <c>null</c>.</param>
        /// <exception cref="ArgumentException">The rows differ in length or do not fit the function, or a variable belongs
        /// to another rule.</exception>
        internal void RebuildRow(FunctionId function, ReadOnlySpan<RuleEntry> before, ReadOnlySpan<RuleEntry> after, RuleVariable? subsume)
        {
            if (before.Length != after.Length || before.IsEmpty)
                throw new ArgumentException($"rebuild_row: the rows have {before.Length} and {after.Length} entries.", nameof(after));

            ThrowIfBuilt();
            var info = egraph.GetFunctionInfo(function);
            CheckRowCount(info, after, "rebuild_row");

            // Check both rows and the subsumption cell before recording the removal, so that a failing call records
            // nothing (a removal without its replacement row would delete the row).
            CheckEntries(before, nameof(before));
            CheckEntries(after, nameof(after));
            if (subsume is RuleVariable cell)
                CheckEntry(cell, nameof(subsume));

            Remove(function, before[..^1]);
            if (subsume is RuleVariable variable)
                SetWithSubsume(function, after, variable);
            else
                Set(function, after);
        }

        /// <summary>
        /// <see cref="Set"/> with an explicit subsumption cell. egglog's <c>set_with_subsume</c>
        /// (egglog-bridge/src/rule.rs:667-692). The cell is ignored when the function cannot subsume.
        /// </summary>
        /// <param name="function">The function.</param>
        /// <param name="entries">One entry per column of the function.</param>
        /// <param name="subsume">The subsumption cell.</param>
        /// <exception cref="ArgumentException">The function is not declared, the entry count is not its column count, or
        /// a variable belongs to another rule.</exception>
        internal void SetWithSubsume(FunctionId function, ReadOnlySpan<RuleEntry> entries, RuleEntry subsume)
        {
            ThrowIfBuilt();
            var info = egraph.GetFunctionInfo(function);
            CheckRowCount(info, entries, "set");
            CheckEntries(entries, nameof(entries));
            CheckEntry(subsume, nameof(subsume));
            query.AddAction(RuleAction.Insert(info.Table, info.Math, entries.ToArray(), subsume));
        }

        /// <summary>
        /// <see cref="Lookup"/> with an explicit subsumption cell for an inserted row. egglog's
        /// <c>lookup_with_subsumed</c> (egglog-bridge/src/rule.rs:524-601).
        /// </summary>
        /// <param name="function">The function.</param>
        /// <param name="keys">The key, one entry per key column.</param>
        /// <param name="subsumed">The subsumption cell of an inserted row.</param>
        /// <param name="panicMessage">Builds the panic message for a failing default.</param>
        /// <returns>The variable holding the output.</returns>
        /// <exception cref="ArgumentException">The function is not declared, the key count is not its key count, or a
        /// variable belongs to another rule.</exception>
        internal RuleVariable LookupWithSubsumed(FunctionId function, ReadOnlySpan<RuleEntry> keys, RuleEntry subsumed, Func<string> panicMessage)
        {
            ArgumentNullException.ThrowIfNull(panicMessage);
            ThrowIfBuilt();
            var info = egraph.GetFunctionInfo(function);
            CheckKeyCount(info, keys, "lookup");
            CheckEntries(keys, nameof(keys));
            CheckEntry(subsumed, nameof(subsumed));

            var result = NewVar(info.OutputType);
            switch (info.Default.Kind)
            {
                case DefaultValKind.FreshId:
                    query.AddAction(RuleAction.LookupOrInsert(info.Table, info.Math, keys.ToArray(), WriteVal.FromCounter(query.IdCounter), subsumed, result.Id));
                    break;
                case DefaultValKind.Const:
                    query.AddAction(RuleAction.LookupOrInsert(info.Table, info.Math, keys.ToArray(), WriteVal.Of(QueryEntry.Const(info.Default.Constant)), subsumed, result.Id));
                    break;
                default:
                {
                    var panic = egraph.NewPanicLazy(panicMessage);
                    query.AddOwnedFunction(panic);
                    query.AddAction(RuleAction.LookupWithFallback(info.Table, info.Math, keys.ToArray(), panic, result.Id));
                    break;
                }
            }

            return result;
        }

        /// <summary>
        /// Adds an atom over <paramref name="table"/> with a fresh timestamp variable and, when the table subsumes, a
        /// subsumption entry. egglog's <c>add_atom_with_timestamp_and_func</c> (egglog-bridge/src/rule.rs:342-385).
        /// </summary>
        /// <remarks>
        /// With <paramref name="function"/>, the layout is the function's and <paramref name="entries"/> must cover its
        /// columns; the subsumption entry is <paramref name="subsume"/> or a fresh variable. Without, the entries are the
        /// table's leading columns and the table has a subsumption column exactly when <paramref name="subsume"/> is
        /// given (the union-find atom of rule-based rebuilding has neither).
        /// </remarks>
        /// <param name="table">The table.</param>
        /// <param name="function">The function the table belongs to, if any.</param>
        /// <param name="subsume">The subsumption entry, or <c>null</c>.</param>
        /// <param name="entries">The entries of the leading columns.</param>
        /// <returns>The atom's id.</returns>
        /// <exception cref="ArgumentException">The entries do not fit the function, or a variable belongs to another
        /// rule.</exception>
        internal RuleAtomId AddAtomWithTimestampAndFunc(TableId table, FunctionId? function, RuleEntry? subsume, ReadOnlySpan<RuleEntry> entries)
        {
            ThrowIfBuilt();
            CheckEntries(entries, nameof(entries));
            if (subsume is RuleEntry s)
                CheckEntry(s, nameof(subsume));

            SchemaMath math;
            if (function is FunctionId f)
            {
                var info = egraph.GetFunctionInfo(f);
                CheckRowCount(info, entries, "add_atom");
                math = info.Math;
            }
            else
            {
                if (entries.IsEmpty)
                    throw new ArgumentException("An atom needs at least one column.", nameof(entries));

                math = new SchemaMath(entries.Length, subsume.HasValue);
            }

            var atom = new RuleEntry[math.TableColumns];
            entries.CopyTo(atom);
            atom[math.TimestampColumn] = NewVar(ColumnTy.Id);
            if (math.Subsume)
                atom[math.SubsumeColumn] = subsume ?? NewVar(ColumnTy.Id);

            return query.AddAtom(new RuleAtom(table, atom, math));
        }

        /// <summary>
        /// Makes semi-naive runs constrain only atom <paramref name="focus"/> to new rows, in a single variant. egglog's
        /// <c>set_focus</c> (egglog-bridge/src/rule.rs:310-312).
        /// </summary>
        /// <param name="focus">The atom's index.</param>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="focus"/> is negative.</exception>
        internal void SetFocus(int focus)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(focus);
            ThrowIfBuilt();
            query.SoleFocus = focus;
        }

        /// <summary>
        /// egglog's <c>assert_has_ty</c> (egglog-bridge/src/rule.rs:276-291): a variable must have the column's type;
        /// constants are untyped.
        /// </summary>
        void AssertHasType(RuleEntry entry, ColumnTy type, string operation, string parameter)
        {
            CheckEntry(entry, parameter);
            if (entry.IsVariable)
            {
                var variableType = query.Variables[entry.Variable.Id.Index].Type;
                if (variableType != type)
                    throw new ArgumentException($"{operation}: mismatch between {entry} and {type}: type mismatch: expected {variableType}, got {type}.", parameter);
            }
        }

        /// <summary>
        /// Throws unless every variable among <paramref name="entries"/> belongs to this rule.
        /// </summary>
        void CheckEntries(ReadOnlySpan<RuleEntry> entries, string parameter)
        {
            foreach (var entry in entries)
                CheckEntry(entry, parameter);
        }

        /// <summary>
        /// Throws unless <paramref name="entry"/> is a constant or a variable of this rule.
        /// </summary>
        void CheckEntry(RuleEntry entry, string parameter)
        {
            if (entry.IsVariable && (uint)entry.Variable.Id.Index >= (uint)query.Variables.Count)
                throw new ArgumentException($"Unknown variable {entry.Variable}: entries must be variables of this rule or constants.", parameter);
        }

        /// <summary>
        /// Throws unless <paramref name="keys"/> has one entry per key column of the function.
        /// </summary>
        static void CheckKeyCount(FunctionInfo info, ReadOnlySpan<RuleEntry> keys, string operation)
        {
            if (keys.Length != info.Math.KeyCount)
                throw new ArgumentException($"{operation}: {info.Name} takes {info.Math.KeyCount} keys, not {keys.Length}.", nameof(keys));
        }

        /// <summary>
        /// Throws unless <paramref name="row"/> has one entry per column of the function.
        /// </summary>
        static void CheckRowCount(FunctionInfo info, ReadOnlySpan<RuleEntry> row, string operation)
        {
            if (row.Length != info.Math.FunctionColumns)
                throw new ArgumentException($"{operation}: {info.Name} has {info.Math.FunctionColumns} columns, not {row.Length}.", nameof(row));
        }

        /// <summary>
        /// Throws once the rule has been built, as egglog's <c>build</c> consumes the builder.
        /// </summary>
        void ThrowIfBuilt()
        {
            if (built)
                throw new InvalidOperationException("The rule has already been built.");
        }

    }

}

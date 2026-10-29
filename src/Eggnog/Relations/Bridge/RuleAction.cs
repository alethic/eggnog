using System;
using System.Text;

namespace Eggnog.Relations
{

    /// <summary>
    /// One recorded step of a rule built with an <see cref="EGraphRuleBuilder"/>, lowered onto core instructions when the
    /// rule is first planned: the data form of the callbacks egglog-bridge's <c>RuleBuilder</c> pushes onto
    /// <c>Query::add_rule</c> (egglog-bridge/src/rule.rs:92-98, 164-716), replayed in recording order by
    /// <see cref="Lower"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A tagged class in place of egglog's boxed closures: <see cref="Kind"/> says which case it is and which fields
    /// are meaningful; there are no virtual calls. Being data, a rule can be printed, validated
    /// (<see cref="Validate"/>) and lowered again for another context.
    /// </para>
    /// <para>
    /// Every write is stamped with the rule's timestamp variable (<see cref="RuleBindings.NextTimestamp"/>), the
    /// <see cref="InstrKind.ReadCounter"/> every lowered rule begins with, so all rows a run writes carry the run's
    /// timestamp. Immutable; the entry arrays are owned by the action. Cold path.
    /// </para>
    /// </remarks>
    internal sealed class RuleAction
    {

        static readonly RuleEntry[] NoEntries = Array.Empty<RuleEntry>();

        readonly RuleEntry[] entries;
        readonly RuleEntry[] others;

        RuleAction(RuleActionKind kind, TableId table, SchemaMath math, ExternalFunctionId function, ExternalFunctionId fallback, RuleEntry[] entries, RuleEntry[] others, RuleEntry entry, RuleEntry other, WriteVal output, VariableId result)
        {
            Kind = kind;
            Table = table;
            Math = math;
            Function = function;
            Fallback = fallback;
            this.entries = entries;
            this.others = others;
            Entry = entry;
            Other = other;
            Output = output;
            Result = result;
        }

        /// <summary>
        /// Gets which case this is.
        /// </summary>
        public RuleActionKind Kind { get; }

        /// <summary>
        /// Gets the table the action reads or writes; the union-find table for <see cref="RuleActionKind.LookupUf"/> and
        /// <see cref="RuleActionKind.Union"/>.
        /// </summary>
        public TableId Table { get; }

        /// <summary>
        /// Gets the layout of <see cref="Table"/> for the cases that build or read function rows.
        /// </summary>
        public SchemaMath Math { get; }

        /// <summary>
        /// Gets the external function called by <see cref="RuleActionKind.CallExternal"/>,
        /// <see cref="RuleActionKind.QueryPrim"/> and <see cref="RuleActionKind.Panic"/>.
        /// </summary>
        public ExternalFunctionId Function { get; }

        /// <summary>
        /// Gets the lazy panic called where <see cref="RuleActionKind.CallExternal"/> or
        /// <see cref="RuleActionKind.LookupWithFallback"/> produces nothing.
        /// </summary>
        public ExternalFunctionId Fallback { get; }

        /// <summary>
        /// Gets the main entry list: the key, the row, the arguments, or the left side of a comparison.
        /// </summary>
        public ReadOnlySpan<RuleEntry> Entries => entries;

        /// <summary>
        /// Gets the right side of <see cref="RuleActionKind.AssertAnyNe"/>.
        /// </summary>
        public ReadOnlySpan<RuleEntry> Others => others;

        /// <summary>
        /// Gets the single entry of the case: the id of <see cref="RuleActionKind.LookupUf"/>, the left id of
        /// <see cref="RuleActionKind.Union"/>, the output of <see cref="RuleActionKind.Subsume"/>, or the subsumption
        /// cell written by <see cref="RuleActionKind.LookupOrInsert"/> and <see cref="RuleActionKind.Insert"/>.
        /// </summary>
        public RuleEntry Entry { get; }

        /// <summary>
        /// Gets the right id of <see cref="RuleActionKind.Union"/>.
        /// </summary>
        public RuleEntry Other { get; }

        /// <summary>
        /// Gets the output a <see cref="RuleActionKind.LookupOrInsert"/> writes for a missing key: a fresh id from the id
        /// counter, or a constant.
        /// </summary>
        public WriteVal Output { get; }

        /// <summary>
        /// Gets the variable the action defines, for the cases that define one.
        /// </summary>
        public VariableId Result { get; }

        /// <summary>
        /// Gets whether the action defines <see cref="Result"/>.
        /// </summary>
        public bool DefinesResult => Kind is RuleActionKind.LookupUf or RuleActionKind.CallExternal or RuleActionKind.LookupOrInsert or RuleActionKind.LookupWithFallback or RuleActionKind.Panic;

        /// <summary>
        /// egglog's <c>lookup_uf</c> callback (egglog-bridge/src/rule.rs:202-214).
        /// </summary>
        /// <param name="unionFind">The union-find table.</param>
        /// <param name="entry">The id to canonicalize.</param>
        /// <param name="result">The variable that receives the canonical id.</param>
        /// <returns>The action.</returns>
        public static RuleAction LookupUf(TableId unionFind, RuleEntry entry, VariableId result) => new(RuleActionKind.LookupUf, unionFind, default, default, default, NoEntries, NoEntries, entry, default, default, result);

        /// <summary>
        /// egglog's <c>check_for_update</c> callback (egglog-bridge/src/rule.rs:221-247).
        /// </summary>
        /// <param name="left">The left entries; owned by the action.</param>
        /// <param name="right">The right entries, as many; owned by the action.</param>
        /// <returns>The action.</returns>
        public static RuleAction AssertAnyNe(RuleEntry[] left, RuleEntry[] right) => new(RuleActionKind.AssertAnyNe, default, default, default, default, left, right, default, default, default, default);

        /// <summary>
        /// egglog's <c>call_external_func</c> callback (egglog-bridge/src/rule.rs:387-405).
        /// </summary>
        /// <param name="function">The external function.</param>
        /// <param name="args">The arguments; owned by the action.</param>
        /// <param name="panic">The lazy panic called where the function produces nothing.</param>
        /// <param name="result">The variable that receives the result.</param>
        /// <returns>The action.</returns>
        public static RuleAction CallExternal(ExternalFunctionId function, RuleEntry[] args, ExternalFunctionId panic, VariableId result) => new(RuleActionKind.CallExternal, default, default, function, panic, args, NoEntries, default, default, default, result);

        /// <summary>
        /// egglog's <c>query_prim</c> callback (egglog-bridge/src/rule.rs:448-470).
        /// </summary>
        /// <param name="function">The primitive.</param>
        /// <param name="entries">The arguments followed by the result entry; owned by the action.</param>
        /// <returns>The action.</returns>
        public static RuleAction QueryPrim(ExternalFunctionId function, RuleEntry[] entries) => new(RuleActionKind.QueryPrim, default, default, function, default, entries, NoEntries, default, default, default, default);

        /// <summary>
        /// The second callback of egglog's <c>subsume</c> (egglog-bridge/src/rule.rs:497-521).
        /// </summary>
        /// <param name="table">The function's table.</param>
        /// <param name="math">Its layout.</param>
        /// <param name="keys">The key; owned by the action.</param>
        /// <param name="output">The output the preceding lookup bound.</param>
        /// <returns>The action.</returns>
        public static RuleAction Subsume(TableId table, SchemaMath math, RuleEntry[] keys, RuleEntry output) => new(RuleActionKind.Subsume, table, math, default, default, keys, NoEntries, output, default, default, default);

        /// <summary>
        /// egglog's <c>lookup_with_subsumed</c> callback for a fresh-id or constant default
        /// (egglog-bridge/src/rule.rs:548-582).
        /// </summary>
        /// <param name="table">The function's table.</param>
        /// <param name="math">Its layout.</param>
        /// <param name="keys">The key; owned by the action.</param>
        /// <param name="output">The output of a missing key.</param>
        /// <param name="subsumed">The subsumption cell of an inserted row; ignored when the table has none.</param>
        /// <param name="result">The variable that receives the output.</param>
        /// <returns>The action.</returns>
        public static RuleAction LookupOrInsert(TableId table, SchemaMath math, RuleEntry[] keys, WriteVal output, RuleEntry subsumed, VariableId result) => new(RuleActionKind.LookupOrInsert, table, math, default, default, keys, NoEntries, subsumed, default, output, result);

        /// <summary>
        /// egglog's <c>lookup_with_subsumed</c> callback for a failing default (egglog-bridge/src/rule.rs:583-597).
        /// </summary>
        /// <param name="table">The function's table.</param>
        /// <param name="math">Its layout.</param>
        /// <param name="keys">The key; owned by the action.</param>
        /// <param name="panic">The lazy panic called where the key is missing.</param>
        /// <param name="result">The variable that receives the output.</param>
        /// <returns>The action.</returns>
        public static RuleAction LookupWithFallback(TableId table, SchemaMath math, RuleEntry[] keys, ExternalFunctionId panic, VariableId result) => new(RuleActionKind.LookupWithFallback, table, math, default, panic, keys, NoEntries, default, default, default, result);

        /// <summary>
        /// egglog's <c>union</c> callback (egglog-bridge/src/rule.rs:626-633).
        /// </summary>
        /// <param name="unionFind">The union-find table.</param>
        /// <param name="left">One id.</param>
        /// <param name="right">The other id.</param>
        /// <returns>The action.</returns>
        public static RuleAction Union(TableId unionFind, RuleEntry left, RuleEntry right) => new(RuleActionKind.Union, unionFind, default, default, default, NoEntries, NoEntries, left, right, default, default);

        /// <summary>
        /// egglog's <c>set_with_subsume</c> callback (egglog-bridge/src/rule.rs:667-692).
        /// </summary>
        /// <param name="table">The function's table.</param>
        /// <param name="math">Its layout.</param>
        /// <param name="row">The function's columns, keys then output; owned by the action.</param>
        /// <param name="subsumed">The subsumption cell; ignored when the table has none.</param>
        /// <returns>The action.</returns>
        public static RuleAction Insert(TableId table, SchemaMath math, RuleEntry[] row, RuleEntry subsumed) => new(RuleActionKind.Insert, table, math, default, default, row, NoEntries, subsumed, default, default, default);

        /// <summary>
        /// egglog's <c>remove</c> callback (egglog-bridge/src/rule.rs:695-703).
        /// </summary>
        /// <param name="table">The function's table.</param>
        /// <param name="keys">The key; owned by the action.</param>
        /// <returns>The action.</returns>
        public static RuleAction Remove(TableId table, RuleEntry[] keys) => new(RuleActionKind.Remove, table, default, default, default, keys, NoEntries, default, default, default, default);

        /// <summary>
        /// egglog's <c>panic</c> callback (egglog-bridge/src/rule.rs:706-715).
        /// </summary>
        /// <param name="panic">The panic function.</param>
        /// <param name="result">The variable bound to its (never produced) result.</param>
        /// <returns>The action.</returns>
        public static RuleAction Panic(ExternalFunctionId panic, VariableId result) => new(RuleActionKind.Panic, default, default, panic, default, NoEntries, NoEntries, default, default, default, result);

        /// <summary>
        /// Appends the action's instructions to <paramref name="rule"/>, translating entries through
        /// <paramref name="bindings"/> and recording the core variables it defines there: the body of the matching egglog
        /// callback.
        /// </summary>
        /// <param name="bindings">The translation environment; updated with the variables the action defines.</param>
        /// <param name="rule">The core rule under construction.</param>
        /// <exception cref="QueryException">The core rejects the instruction (an arity mismatch the builder did not
        /// catch).</exception>
        public void Lower(RuleBindings bindings, RuleBuilder rule)
        {
            ArgumentNullException.ThrowIfNull(bindings);
            ArgumentNullException.ThrowIfNull(rule);

            switch (Kind)
            {
                case RuleActionKind.LookupUf:
                {
                    // Column 1 of the union-find is the canonical id; an id never displaced is its own (rule.rs:209).
                    var id = bindings.Convert(Entry);
                    var canonical = rule.LookupWithDefault(Table, [id], bindings.ConvertLoose(Entry), new ColumnId(1));
                    bindings.Bind(Result, canonical);
                    break;
                }
                case RuleActionKind.AssertAnyNe:
                    rule.AssertAnyNe(bindings.ConvertAllLoose(entries), bindings.ConvertAllLoose(others));
                    break;
                case RuleActionKind.CallExternal:
                {
                    // External functions that fail on the right-hand side panic (rule.rs:396-400).
                    var result = rule.CallExternalWithFallback(Function, bindings.ConvertAllLoose(entries), Fallback, ReadOnlySpan<QueryEntry>.Empty);
                    bindings.Bind(Result, result);
                    break;
                }
                case RuleActionKind.QueryPrim:
                    LowerQueryPrim(bindings, rule);
                    break;
                case RuleActionKind.Subsume:
                    LowerSubsume(bindings, rule);
                    break;
                case RuleActionKind.LookupOrInsert:
                    LowerLookupOrInsert(bindings, rule);
                    break;
                case RuleActionKind.LookupWithFallback:
                {
                    var result = rule.LookupWithFallback(Table, bindings.ConvertAll(entries), new ColumnId(Math.OutputColumn), Fallback, ReadOnlySpan<QueryEntry>.Empty);
                    bindings.Bind(Result, result);
                    break;
                }
                case RuleActionKind.Union:
                    rule.Insert(Table, [bindings.Convert(Entry), bindings.Convert(Other), bindings.NextTimestamp]);
                    break;
                case RuleActionKind.Insert:
                    rule.Insert(Table, BuildRow(bindings, entries, Math, default, Entry, hasOutput: false));

                    // A head write into the contradiction function stops a case's search early (so does a lookup-or-insert).
                    if (bindings.TryGetStop(Table, out var stop))
                        rule.CallExternal(stop, ReadOnlySpan<QueryEntry>.Empty);

                    break;
                case RuleActionKind.Remove:
                    rule.Remove(Table, bindings.ConvertAll(entries));
                    break;
                case RuleActionKind.Panic:
                {
                    var result = rule.CallExternal(Function, ReadOnlySpan<QueryEntry>.Empty);
                    bindings.Bind(Result, result);
                    break;
                }
                default:
                    throw new InvalidOperationException($"Unknown rule action {Kind}.");
            }
        }

        /// <summary>
        /// Checks that every variable the action reads is bound when it runs (by an atom, or defined by an earlier action)
        /// and records the variables it binds, following <see cref="Lower"/> exactly.
        /// </summary>
        /// <remarks>
        /// egglog leaves a read of an unbound variable to the core, where the lowered program reads a slot the join never
        /// fills, which is undefined (core-relations/src/action/mod.rs:225-235); here such a rule is rejected when it is
        /// built. <paramref name="grounded"/> is egglog's <c>Bindings::grounded</c>: the variables atoms bind plus those
        /// a <see cref="RuleActionKind.QueryPrim"/> bound, which decides whether a primitive's result entry is bound or
        /// compared.
        /// </remarks>
        /// <param name="bound">The variables bound so far; updated.</param>
        /// <param name="grounded">The variables atoms and primitives bound so far; updated.</param>
        /// <param name="index">The action's position, for the message.</param>
        /// <exception cref="InvalidOperationException">The action reads an unbound variable.</exception>
        public void Validate(FixedBitSet bound, FixedBitSet grounded, int index)
        {
            ArgumentNullException.ThrowIfNull(bound);
            ArgumentNullException.ThrowIfNull(grounded);

            switch (Kind)
            {
                case RuleActionKind.LookupUf:
                case RuleActionKind.Subsume:
                    CheckBound(Entry, bound, index);
                    CheckBound(entries, bound, index);
                    break;
                case RuleActionKind.AssertAnyNe:
                    CheckBound(entries, bound, index);
                    CheckBound(others, bound, index);
                    break;
                case RuleActionKind.CallExternal:
                case RuleActionKind.LookupWithFallback:
                case RuleActionKind.Remove:
                    CheckBound(entries, bound, index);
                    break;
                case RuleActionKind.QueryPrim:
                {
                    CheckBound(entries.AsSpan(0, entries.Length - 1), bound, index);
                    var last = entries[^1];
                    if (last.IsVariable && grounded.Contains(last.Variable.Id.Index) == false)
                    {
                        bound.Insert(last.Variable.Id.Index);
                        grounded.Insert(last.Variable.Id.Index);
                    }
                    else
                    {
                        CheckBound(last, bound, index);
                    }

                    break;
                }
                case RuleActionKind.LookupOrInsert:
                    CheckBound(entries, bound, index);
                    if (Math.Subsume)
                        CheckBound(Entry, bound, index);

                    break;
                case RuleActionKind.Union:
                    CheckBound(Entry, bound, index);
                    CheckBound(Other, bound, index);
                    break;
                case RuleActionKind.Insert:
                    CheckBound(entries, bound, index);
                    if (Math.Subsume)
                        CheckBound(Entry, bound, index);

                    break;
            }

            if (DefinesResult)
                bound.Insert(Result.Index);
        }

        /// <inheritdoc />
        public override string ToString()
        {
            var b = new StringBuilder();
            switch (Kind)
            {
                case RuleActionKind.LookupUf:
                    b.Append(Result).Append(" = find(").Append(Entry).Append(')');
                    break;
                case RuleActionKind.AssertAnyNe:
                    b.Append("assert_any_ne([").AppendJoin(", ", entries).Append("], [").AppendJoin(", ", others).Append("])");
                    break;
                case RuleActionKind.CallExternal:
                    b.Append(Result).Append(" = ").Append(Function).Append('(').AppendJoin(", ", entries).Append(") or panic ").Append(Fallback);
                    break;
                case RuleActionKind.QueryPrim:
                    b.Append(Function).Append('(').AppendJoin(", ", entries[..^1]).Append(") -> ").Append(entries[^1]);
                    break;
                case RuleActionKind.Subsume:
                    b.Append("subsume ").Append(Table).Append('[').AppendJoin(", ", entries).Append("] = ").Append(Entry);
                    break;
                case RuleActionKind.LookupOrInsert:
                    b.Append(Result).Append(" = ").Append(Table).Append('[').AppendJoin(", ", entries).Append("] or insert ").Append(Output);
                    break;
                case RuleActionKind.LookupWithFallback:
                    b.Append(Result).Append(" = ").Append(Table).Append('[').AppendJoin(", ", entries).Append("] or panic ").Append(Fallback);
                    break;
                case RuleActionKind.Union:
                    b.Append("union(").Append(Entry).Append(", ").Append(Other).Append(')');
                    break;
                case RuleActionKind.Insert:
                    b.Append("set ").Append(Table).Append('(').AppendJoin(", ", entries).Append(')');
                    if (Math.Subsume)
                        b.Append(" subsumed ").Append(Entry);

                    break;
                case RuleActionKind.Remove:
                    b.Append("remove ").Append(Table).Append('[').AppendJoin(", ", entries).Append(']');
                    break;
                case RuleActionKind.Panic:
                    b.Append("panic ").Append(Function);
                    break;
            }

            return b.ToString();
        }

        /// <summary>
        /// The body of egglog's <c>query_prim</c> callback (rule.rs:456-468): call the primitive on every entry but the
        /// last; if the last is a variable no atom or earlier primitive bound, bind it to the result, otherwise keep only
        /// the lanes where the result equals it. A lane where the primitive produces nothing halts, so a failing
        /// left-hand-side primitive silently drops the match.
        /// </summary>
        void LowerQueryPrim(RuleBindings bindings, RuleBuilder rule)
        {
            var converted = bindings.ConvertAllLoose(entries);
            var expected = converted[^1];
            var result = rule.CallExternal(Function, converted.AsSpan(0, converted.Length - 1));
            var last = entries[^1];
            if (last.IsVariable && bindings.IsGrounded(last.Variable.Id) == false)
            {
                bindings.Bind(last.Variable.Id, result);
                bindings.Ground(last.Variable.Id);
            }
            else
            {
                rule.AssertEq(result, expected);
            }
        }

        /// <summary>
        /// The body of egglog's second <c>subsume</c> callback (rule.rs:497-521): read the key's subsumption cell and,
        /// where it is not subsumed, insert the row <c>keys ++ [output, ts, SUBSUMED]</c>.
        /// </summary>
        /// <remarks>
        /// <para>
        /// egglog reads the cell with a plain <c>lookup</c> of the committed table (egglog-bridge/src/rule.rs:477-519),
        /// so for a key the table lacks (whose subsumed row the preceding lookup only staged) the lane halts and every
        /// later action of the rule is skipped for that match. Worse, when an earlier lookup in the same batch
        /// predicted the key's row as not subsumed, the preceding lookup returns that prediction and the subsumption is
        /// lost.
        /// </para>
        /// <para>
        /// Here the cell is read with a <c>LookupOrInsertDefault</c> of the subsumption column, which reads the committed
        /// row or else the row this batch's state predicted for the key, and drops no lane. The preceding lookup (of the
        /// same key, recorded right before by <see cref="EGraphRuleBuilder.Subsume"/>) leaves the key of every live lane
        /// committed or predicted, so the read never inserts. It sees the committed cell, as egglog does; or the row that
        /// lookup has just staged for a missing key, already subsumed, so nothing more is staged (egglog stages that row
        /// once too, then halts); or a row an earlier lookup predicted as not subsumed, so the subsumed row is staged
        /// (egglog loses it). The subsumed row is staged at most once per lane: where egglog stages it, plus the case
        /// egglog drops. The read's defaults are the subsumed row itself, so even an insertion would stage the right row
        /// once.
        /// </para>
        /// </remarks>
        void LowerSubsume(RuleBindings bindings, RuleBuilder rule)
        {
            var math = Math;
            var keys = bindings.ConvertAll(entries);
            var output = bindings.Convert(Entry);
            var subsumed = QueryEntry.Const(new Value(SchemaMath.Subsumed));
            var defaults = new WriteVal[math.TableColumns - math.KeyCount];
            for (var i = math.KeyCount; i < math.TableColumns; i++)
            {
                if (i == math.TimestampColumn)
                    defaults[i - math.KeyCount] = WriteVal.Of(bindings.NextTimestamp);
                else if (i == math.OutputColumn)
                    defaults[i - math.KeyCount] = WriteVal.Of(output);
                else
                    defaults[i - math.KeyCount] = WriteVal.Of(subsumed);
            }

            var current = rule.LookupOrInsert(Table, keys, defaults, new ColumnId(math.SubsumeColumn));
            var row = BuildRow(bindings, entries, math, Entry, RuleEntry.Const(new Value(SchemaMath.Subsumed), ColumnTy.Id), hasOutput: true);
            rule.InsertIfEq(Table, current, QueryEntry.Const(new Value(SchemaMath.NotSubsumed)), row);
        }

        /// <summary>
        /// The body of egglog's <c>lookup_with_subsumed</c> callback for a default (rule.rs:554-581): look the key up and,
        /// where it is missing, insert <c>keys ++ [output, ts, subsumed?]</c>, the output being a fresh id or the
        /// constant; bind the row's output.
        /// </summary>
        void LowerLookupOrInsert(RuleBindings bindings, RuleBuilder rule)
        {
            var math = Math;
            var defaults = new WriteVal[math.TableColumns - math.KeyCount];
            for (var i = math.KeyCount; i < math.TableColumns; i++)
            {
                if (i == math.TimestampColumn)
                    defaults[i - math.KeyCount] = WriteVal.Of(bindings.NextTimestamp);
                else if (i == math.OutputColumn)
                    defaults[i - math.KeyCount] = Output;
                else
                    defaults[i - math.KeyCount] = WriteVal.Of(bindings.Convert(Entry));
            }

            var keys = bindings.ConvertAll(entries);
            var result = rule.LookupOrInsert(Table, keys, defaults, new ColumnId(math.OutputColumn));
            bindings.Bind(Result, result);

            // A head write of the contradiction function's row stops a case's search early, as an insert does (the rule
            // layer writes its contradiction relation through a lookup-or-insert); a lookup that finds the row held
            // stops it too, which is no change, since the row's merge stops the case anyway.
            if (bindings.TryGetStop(Table, out var stop))
                rule.CallExternal(stop, ReadOnlySpan<QueryEntry>.Empty);
        }

        /// <summary>
        /// Builds a physical row from the function columns and the rule's timestamp: egglog's <c>write_table_row</c>
        /// (egglog-bridge/src/lib.rs:1573-1595). With <paramref name="hasOutput"/>, <paramref name="columns"/> is the key
        /// and <paramref name="output"/> fills the output column; otherwise <paramref name="columns"/> already holds it.
        /// </summary>
        static QueryEntry[] BuildRow(RuleBindings bindings, RuleEntry[] columns, SchemaMath math, RuleEntry output, RuleEntry subsumed, bool hasOutput)
        {
            var row = new QueryEntry[math.TableColumns];
            for (var i = 0; i < columns.Length; i++)
                row[i] = bindings.Convert(columns[i]);

            if (hasOutput)
                row[math.OutputColumn] = bindings.Convert(output);

            row[math.TimestampColumn] = bindings.NextTimestamp;
            if (math.Subsume)
                row[math.SubsumeColumn] = bindings.Convert(subsumed);

            return row;
        }

        /// <summary>
        /// Throws unless every variable among <paramref name="entries"/> is bound.
        /// </summary>
        static void CheckBound(ReadOnlySpan<RuleEntry> entries, FixedBitSet bound, int index)
        {
            foreach (var entry in entries)
                CheckBound(entry, bound, index);
        }

        /// <summary>
        /// Throws unless <paramref name="entry"/> is a constant or a bound variable.
        /// </summary>
        static void CheckBound(RuleEntry entry, FixedBitSet bound, int index)
        {
            if (entry.IsVariable && bound.Contains(entry.Variable.Id.Index) == false)
                throw new InvalidOperationException($"Action {index} of the rule reads {entry.Variable}, which no atom binds and no earlier action defines.");
        }

    }

}

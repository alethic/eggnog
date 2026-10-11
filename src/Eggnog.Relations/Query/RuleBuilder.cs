using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace Eggnog.Relations
{

    /// <summary>
    /// Builds the right-hand side of a rule, an <see cref="Instr"/> program over the variables its query binds, then
    /// adds the rule. egglog's <c>RuleBuilder</c> (core-relations/src/query.rs:514-874), from
    /// <see cref="RuleQueryBuilder.Build"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every method appends one instruction. A method that produces a value creates a fresh variable for it and returns
    /// it (egglog's <c>mark_defined</c>); every variable an instruction reads is marked used (egglog's
    /// <c>mark_used</c>), so <see cref="Build(string)"/> can compute the variables the join must hand over: used and not
    /// defined (<see cref="ActionInfo.UsedVariables"/>). Operands are query entries: variables of this rule or
    /// constants; wildcards are not operands.
    /// </para>
    /// <para>
    /// Each method validates its input against the table's schema before changing anything, so a failing call leaves
    /// the rule as it was: no variable, no instruction, no marks. egglog's checks are kept with its error kinds
    /// (<see cref="QueryException"/>); additionally the defaults of <see cref="LookupOrInsert"/> must fill the value
    /// columns exactly and destination columns must exist, which egglog leaves to fail at run time.
    /// </para>
    /// <para>
    /// Usable while it is the open rule of its <see cref="RuleSetBuilder"/> and until <see cref="Build(string)"/>;
    /// afterwards every member throws <see cref="InvalidOperationException"/>. Not thread-safe. Cold path.
    /// </para>
    /// </remarks>
    internal sealed class RuleBuilder
    {

        readonly RuleSetBuilder owner;
        readonly RuleQueryBuilder query;
        readonly List<Instr> instrs = new();

        /// <summary>
        /// Initializes a new instance. Called by <see cref="RuleQueryBuilder.Build"/>.
        /// </summary>
        /// <param name="owner">The rule set builder.</param>
        /// <param name="query">The finished query builder of the rule.</param>
        internal RuleBuilder(RuleSetBuilder owner, RuleQueryBuilder query)
        {
            ArgumentNullException.ThrowIfNull(owner);
            ArgumentNullException.ThrowIfNull(query);
            this.owner = owner;
            this.query = query;
        }

        /// <summary>
        /// Gets the number of instructions appended so far.
        /// </summary>
        public int InstrCount => instrs.Count;

        /// <summary>
        /// Plans the query and adds the rule with an empty description. egglog's <c>build</c>.
        /// </summary>
        /// <returns>The rule's id; its action has the same index.</returns>
        public RuleId Build() => Build(string.Empty);

        /// <summary>
        /// Plans the query and adds the rule. egglog's <c>build_with_description</c>
        /// (core-relations/src/query.rs:551-576): the action's used variables are the ones read and not defined, in
        /// ascending order; the query is planned once, here, with the statistics of the current tables; the header
        /// subsets are evaluated for the rule set being built. The builder cannot be used afterwards, also when this
        /// throws.
        /// </summary>
        /// <param name="description">The rule's description, which keys its report.</param>
        /// <returns>The rule's id; its action has the same index.</returns>
        /// <exception cref="InvalidOperationException">The program reads a variable that no atom binds and no
        /// instruction defines.</exception>
        public RuleId Build(string description)
        {
            ArgumentNullException.ThrowIfNull(description);
            owner.ThrowIfNotOpen(this);
            return owner.AddBuiltRule(this, query.Query, instrs.ToArray(), query.IdConstants.ToArray(), description);
        }

        /// <summary>
        /// Reads <paramref name="counter"/> into a new variable. egglog's <c>read_counter</c>.
        /// </summary>
        /// <param name="counter">The counter.</param>
        /// <returns>The variable holding the counter's value.</returns>
        public Variable ReadCounter(CounterId counter)
        {
            owner.ThrowIfNotOpen(this);
            var dst = NextVariable();
            Commit(new ReadCounterInstr(counter, dst), dst);
            return dst;
        }

        /// <summary>
        /// Looks <paramref name="args"/> up in <paramref name="table"/> and binds column <paramref name="column"/> of
        /// the row to a new variable; a key the table lacks gets the row <c>args ++ defaults</c>, staged once per
        /// execution state. egglog's <c>lookup_or_insert</c>, the instruction behind constructors.
        /// </summary>
        /// <param name="table">The table.</param>
        /// <param name="args">The key: one entry per key column.</param>
        /// <param name="defaults">The value columns of a new row, in column order: one per value column.</param>
        /// <param name="column">The column of the row to bind.</param>
        /// <returns>The variable holding the column.</returns>
        /// <exception cref="QueryException">The key does not fit the table (<see cref="QueryErrorKind.KeyArityMismatch"/>)
        /// or the defaults do not fill its value columns (<see cref="QueryErrorKind.TableArityMismatch"/>).</exception>
        /// <exception cref="ArgumentOutOfRangeException">The table has no such column, or a current-value default copies
        /// a cell that is not earlier in the row.</exception>
        /// <exception cref="ArgumentException">An operand is a wildcard or a variable of another rule.</exception>
        public Variable LookupOrInsert(TableId table, ReadOnlySpan<QueryEntry> args, ReadOnlySpan<WriteVal> defaults, ColumnId column)
        {
            owner.ThrowIfNotOpen(this);
            var spec = GetSpec(table);
            ValidateKeys(table, spec, args);
            ValidateDefaults(table, spec, defaults);
            ValidateColumn(spec, column);
            var dst = NextVariable();
            var instr = new LookupOrInsertDefaultInstr(table, args, defaults, column, dst);
            CheckKnown(args, nameof(args));
            foreach (var d in defaults)
                if (d.Kind == WriteValKind.Entry)
                    CheckKnown(d.Entry, nameof(defaults));

            Commit(instr, dst);
            MarkUsed(args);
            foreach (var d in defaults)
                if (d.Kind == WriteValKind.Entry)
                    MarkUsed(d.Entry);

            return dst;
        }

        /// <summary>
        /// Looks <paramref name="args"/> up in <paramref name="table"/> and binds column <paramref name="column"/> of
        /// the row to a new variable, or <paramref name="default"/> when the table lacks the key. egglog's
        /// <c>lookup_with_default</c>.
        /// </summary>
        /// <param name="table">The table.</param>
        /// <param name="args">The key: one entry per key column.</param>
        /// <param name="default">The value when the key is missing.</param>
        /// <param name="column">The column of the row to bind.</param>
        /// <returns>The variable holding the column or the default.</returns>
        /// <exception cref="QueryException">The key does not fit the table
        /// (<see cref="QueryErrorKind.KeyArityMismatch"/>).</exception>
        /// <exception cref="ArgumentOutOfRangeException">The table has no such column.</exception>
        /// <exception cref="ArgumentException">An operand is a wildcard or a variable of another rule.</exception>
        public Variable LookupWithDefault(TableId table, ReadOnlySpan<QueryEntry> args, QueryEntry @default, ColumnId column)
        {
            owner.ThrowIfNotOpen(this);
            var spec = GetSpec(table);
            ValidateKeys(table, spec, args);
            ValidateColumn(spec, column);
            var dst = NextVariable();
            var instr = new LookupWithDefaultInstr(table, args, column, dst, @default);
            CheckKnown(args, nameof(args));
            CheckKnown(@default, nameof(@default));
            Commit(instr, dst);
            MarkUsed(args);
            MarkUsed(@default);
            return dst;
        }

        /// <summary>
        /// Looks <paramref name="args"/> up in <paramref name="table"/> and binds column <paramref name="column"/> of
        /// the row to a new variable; a lane whose key the table lacks halts. egglog's <c>lookup</c>.
        /// </summary>
        /// <param name="table">The table.</param>
        /// <param name="args">The key: one entry per key column.</param>
        /// <param name="column">The column of the row to bind.</param>
        /// <returns>The variable holding the column.</returns>
        /// <exception cref="QueryException">The key does not fit the table
        /// (<see cref="QueryErrorKind.KeyArityMismatch"/>).</exception>
        /// <exception cref="ArgumentOutOfRangeException">The table has no such column.</exception>
        /// <exception cref="ArgumentException">An operand is a wildcard or a variable of another rule.</exception>
        public Variable Lookup(TableId table, ReadOnlySpan<QueryEntry> args, ColumnId column)
        {
            owner.ThrowIfNotOpen(this);
            var spec = GetSpec(table);
            ValidateKeys(table, spec, args);
            ValidateColumn(spec, column);
            var dst = NextVariable();
            var instr = new LookupInstr(table, args, column, dst);
            CheckKnown(args, nameof(args));
            Commit(instr, dst);
            MarkUsed(args);
            return dst;
        }

        /// <summary>
        /// Inserts <paramref name="values"/> into <paramref name="table"/>. egglog's <c>insert</c>.
        /// </summary>
        /// <param name="table">The table.</param>
        /// <param name="values">The row: one entry per column.</param>
        /// <exception cref="QueryException">The row does not fit the table
        /// (<see cref="QueryErrorKind.TableArityMismatch"/>).</exception>
        /// <exception cref="ArgumentException">An operand is a wildcard or a variable of another rule.</exception>
        public void Insert(TableId table, ReadOnlySpan<QueryEntry> values)
        {
            owner.ThrowIfNotOpen(this);
            ValidateRow(table, GetSpec(table), values);
            var instr = new InsertInstr(table, values);
            CheckKnown(values, nameof(values));
            Commit(instr);
            MarkUsed(values);
        }

        /// <summary>
        /// Inserts <paramref name="values"/> into <paramref name="table"/> in the lanes where <paramref name="left"/>
        /// equals <paramref name="right"/>. egglog's <c>insert_if_eq</c>.
        /// </summary>
        /// <param name="table">The table.</param>
        /// <param name="left">The left operand of the comparison.</param>
        /// <param name="right">The right operand of the comparison.</param>
        /// <param name="values">The row: one entry per column.</param>
        /// <exception cref="QueryException">The row does not fit the table
        /// (<see cref="QueryErrorKind.TableArityMismatch"/>).</exception>
        /// <exception cref="ArgumentException">An operand is a wildcard or a variable of another rule.</exception>
        public void InsertIfEq(TableId table, QueryEntry left, QueryEntry right, ReadOnlySpan<QueryEntry> values)
        {
            owner.ThrowIfNotOpen(this);
            ValidateRow(table, GetSpec(table), values);
            var instr = new InsertIfEqInstr(table, left, right, values);
            CheckKnown(values, nameof(values));
            CheckKnown(left, nameof(left));
            CheckKnown(right, nameof(right));
            Commit(instr);
            MarkUsed(values);
            MarkUsed(left);
            MarkUsed(right);
        }

        /// <summary>
        /// Removes the row with key <paramref name="args"/> from <paramref name="table"/>, if it is there. egglog's
        /// <c>remove</c>.
        /// </summary>
        /// <param name="table">The table.</param>
        /// <param name="args">The key: one entry per key column.</param>
        /// <exception cref="QueryException">The key does not fit the table
        /// (<see cref="QueryErrorKind.KeyArityMismatch"/>).</exception>
        /// <exception cref="ArgumentException">An operand is a wildcard or a variable of another rule.</exception>
        public void Remove(TableId table, ReadOnlySpan<QueryEntry> args)
        {
            owner.ThrowIfNotOpen(this);
            ValidateKeys(table, GetSpec(table), args);
            var instr = new RemoveInstr(table, args);
            CheckKnown(args, nameof(args));
            Commit(instr);
            MarkUsed(args);
        }

        /// <summary>
        /// Calls <paramref name="function"/> on <paramref name="args"/> and binds the result to a new variable; a lane
        /// where it produces nothing halts. egglog's <c>call_external</c>.
        /// </summary>
        /// <param name="function">The external function.</param>
        /// <param name="args">The arguments.</param>
        /// <returns>The variable holding the result.</returns>
        /// <exception cref="ArgumentException">An operand is a wildcard or a variable of another rule.</exception>
        public Variable CallExternal(ExternalFunctionId function, ReadOnlySpan<QueryEntry> args)
        {
            owner.ThrowIfNotOpen(this);
            var dst = NextVariable();
            var instr = new ExternalInstr(function, args, dst);
            CheckKnown(args, nameof(args));
            Commit(instr, dst);
            MarkUsed(args);
            return dst;
        }

        /// <summary>
        /// Looks <paramref name="key"/> up in <paramref name="table"/> and binds column <paramref name="column"/> of the
        /// row to a new variable; where the table lacks the key, calls <paramref name="function"/> on
        /// <paramref name="functionArgs"/> instead, and a lane where that produces nothing halts. egglog's
        /// <c>lookup_with_fallback</c>.
        /// </summary>
        /// <param name="table">The table.</param>
        /// <param name="key">The key: one entry per key column.</param>
        /// <param name="column">The column of the row to bind.</param>
        /// <param name="function">The fallback.</param>
        /// <param name="functionArgs">The fallback's arguments.</param>
        /// <returns>The variable holding the column or the fallback's result.</returns>
        /// <exception cref="QueryException">The key does not fit the table
        /// (<see cref="QueryErrorKind.KeyArityMismatch"/>).</exception>
        /// <exception cref="ArgumentOutOfRangeException">The table has no such column.</exception>
        /// <exception cref="ArgumentException">An operand is a wildcard or a variable of another rule.</exception>
        public Variable LookupWithFallback(TableId table, ReadOnlySpan<QueryEntry> key, ColumnId column, ExternalFunctionId function, ReadOnlySpan<QueryEntry> functionArgs)
        {
            owner.ThrowIfNotOpen(this);
            var spec = GetSpec(table);
            ValidateKeys(table, spec, key);
            ValidateColumn(spec, column);
            var dst = NextVariable();
            var instr = new LookupWithFallbackInstr(table, key, function, functionArgs, column, dst);
            CheckKnown(key, nameof(key));
            CheckKnown(functionArgs, nameof(functionArgs));
            Commit(instr, dst);
            MarkUsed(key);
            MarkUsed(functionArgs);
            return dst;
        }

        /// <summary>
        /// Calls <paramref name="function"/> on <paramref name="args"/> and binds the result to a new variable; where it
        /// produces nothing, calls <paramref name="fallback"/> on <paramref name="fallbackArgs"/> instead, and a lane
        /// where that produces nothing halts. egglog's <c>call_external_with_fallback</c>.
        /// </summary>
        /// <param name="function">The external function.</param>
        /// <param name="args">Its arguments.</param>
        /// <param name="fallback">The fallback.</param>
        /// <param name="fallbackArgs">The fallback's arguments.</param>
        /// <returns>The variable holding the result.</returns>
        /// <exception cref="ArgumentException">An operand is a wildcard or a variable of another rule.</exception>
        public Variable CallExternalWithFallback(ExternalFunctionId function, ReadOnlySpan<QueryEntry> args, ExternalFunctionId fallback, ReadOnlySpan<QueryEntry> fallbackArgs)
        {
            owner.ThrowIfNotOpen(this);
            var dst = NextVariable();
            var instr = new ExternalWithFallbackInstr(function, args, fallback, fallbackArgs, dst);
            CheckKnown(args, nameof(args));
            CheckKnown(fallbackArgs, nameof(fallbackArgs));
            Commit(instr, dst);
            MarkUsed(args);
            MarkUsed(fallbackArgs);
            return dst;
        }

        /// <summary>
        /// Continues only in the lanes where <paramref name="left"/> equals <paramref name="right"/>. egglog's
        /// <c>assert_eq</c>.
        /// </summary>
        /// <param name="left">The left operand.</param>
        /// <param name="right">The right operand.</param>
        /// <exception cref="ArgumentException">An operand is a wildcard or a variable of another rule.</exception>
        public void AssertEq(QueryEntry left, QueryEntry right)
        {
            owner.ThrowIfNotOpen(this);
            var instr = new AssertEqInstr(left, right);
            CheckKnown(left, nameof(left));
            CheckKnown(right, nameof(right));
            Commit(instr);
            MarkUsed(left);
            MarkUsed(right);
        }

        /// <summary>
        /// Continues only in the lanes where <paramref name="left"/> differs from <paramref name="right"/>. egglog's
        /// <c>assert_ne</c>.
        /// </summary>
        /// <param name="left">The left operand.</param>
        /// <param name="right">The right operand.</param>
        /// <exception cref="ArgumentException">An operand is a wildcard or a variable of another rule.</exception>
        public void AssertNe(QueryEntry left, QueryEntry right)
        {
            owner.ThrowIfNotOpen(this);
            var instr = new AssertNeInstr(left, right);
            CheckKnown(left, nameof(left));
            CheckKnown(right, nameof(right));
            Commit(instr);
            MarkUsed(left);
            MarkUsed(right);
        }

        /// <summary>
        /// Continues only in the lanes where <c>left[i] != right[i]</c> for some <c>i</c>, the check of egglog-style
        /// rebuilding. egglog's <c>assert_any_ne</c>: one instruction over <c>left ++ right</c> divided at
        /// <c>left.Length</c>.
        /// </summary>
        /// <param name="left">The left operands.</param>
        /// <param name="right">The right operands, as many as the left ones.</param>
        /// <exception cref="QueryException">The lists differ in length
        /// (<see cref="QueryErrorKind.MultiComparisonMismatch"/>).</exception>
        /// <exception cref="ArgumentException">An operand is a wildcard or a variable of another rule.</exception>
        public void AssertAnyNe(ReadOnlySpan<QueryEntry> left, ReadOnlySpan<QueryEntry> right)
        {
            owner.ThrowIfNotOpen(this);
            if (left.Length != right.Length)
                throw QueryException.MultiComparisonMismatch(left.Length, right.Length);

            var ops = new QueryEntry[left.Length + right.Length];
            left.CopyTo(ops);
            right.CopyTo(ops.AsSpan(left.Length));
            var instr = new AssertAnyNeInstr(ops, left.Length);
            CheckKnown(ops, nameof(left));
            Commit(instr);
            MarkUsed(ops);
        }

        /// <summary>
        /// Gets the schema of <paramref name="table"/>. egglog's <c>table_info</c>.
        /// </summary>
        TableSpec GetSpec(TableId table) => owner.Database.GetTableInfo(table).Spec;

        /// <summary>
        /// Gets the variable the next definition creates, without creating it, so the instruction can be built (and
        /// validate itself) before anything changes.
        /// </summary>
        Variable NextVariable() => new(query.VariableCount);

        /// <summary>
        /// Appends <paramref name="instr"/>, after which nothing may fail.
        /// </summary>
        void Commit(Instr instr) => instrs.Add(instr);

        /// <summary>
        /// Creates <paramref name="dst"/>, appends <paramref name="instr"/> that defines it and marks it defined.
        /// </summary>
        void Commit(Instr instr, Variable dst)
        {
            var created = query.CreateVariable(null);
            Debug.Assert(created == dst, "the destination must be the variable created next");
            instrs.Add(instr);
            query.Query.MarkDefined(created);
        }

        /// <summary>
        /// Marks the variables among <paramref name="entries"/> as read by the action. egglog's <c>mark_used</c>.
        /// </summary>
        void MarkUsed(ReadOnlySpan<QueryEntry> entries)
        {
            foreach (var e in entries)
                MarkUsed(e);
        }

        /// <summary>
        /// Marks <paramref name="entry"/>, if a variable, as read by the action.
        /// </summary>
        void MarkUsed(QueryEntry entry)
        {
            if (entry.IsVariable)
                query.Query.MarkUsed(entry.Variable);
        }

        /// <summary>
        /// Throws unless every variable among <paramref name="entries"/> was created by this rule.
        /// </summary>
        void CheckKnown(ReadOnlySpan<QueryEntry> entries, string parameter)
        {
            foreach (var e in entries)
                CheckKnown(e, parameter);
        }

        /// <summary>
        /// Throws unless <paramref name="entry"/> is a constant or a variable created by this rule (and so not the
        /// destination being defined).
        /// </summary>
        void CheckKnown(QueryEntry entry, string parameter)
        {
            if (entry.IsVariable && query.IsKnown(entry.Variable) == false)
                throw new ArgumentException($"Unknown variable {entry.Variable}: operands must be variables of this rule or constants.", parameter);
        }

        /// <summary>
        /// egglog's <c>validate_row</c>: a row has one entry per column.
        /// </summary>
        static void ValidateRow(TableId table, TableSpec spec, ReadOnlySpan<QueryEntry> values)
        {
            if (values.Length != spec.Arity)
                throw QueryException.TableArityMismatch(table, spec.Arity, values.Length);
        }

        /// <summary>
        /// egglog's <c>validate_keys</c>: a key has one entry per key column.
        /// </summary>
        static void ValidateKeys(TableId table, TableSpec spec, ReadOnlySpan<QueryEntry> keys)
        {
            if (keys.Length != spec.KeyCount)
                throw QueryException.KeyArityMismatch(table, spec.KeyCount, keys.Length);
        }

        /// <summary>
        /// egglog's <c>validate_vals</c>, which rejects a default beyond the last column (reporting that column), plus
        /// the check egglog lacks: the defaults must reach the last column, or the instruction would stage a short row
        /// (core-relations/src/query.rs:856-873).
        /// </summary>
        static void ValidateDefaults(TableId table, TableSpec spec, ReadOnlySpan<WriteVal> defaults)
        {
            if (spec.KeyCount + defaults.Length > spec.Arity)
                throw QueryException.TableArityMismatch(table, spec.Arity, spec.Arity);
            if (spec.KeyCount + defaults.Length < spec.Arity)
                throw QueryException.TableArityMismatch(table, spec.Arity, spec.KeyCount + defaults.Length);
        }

        /// <summary>
        /// Rejects a destination column the table does not have; egglog never validates <c>dst_col</c> in
        /// <c>lookup_or_insert</c>, <c>lookup_with_default</c>, <c>lookup</c> or <c>lookup_with_fallback</c>
        /// (core-relations/src/query.rs:592-763) and fails when the lookup runs.
        /// </summary>
        static void ValidateColumn(TableSpec spec, ColumnId column)
        {
            if ((uint)column.Rep >= (uint)spec.Arity)
                throw new ArgumentOutOfRangeException(nameof(column), column, $"The table has {spec.Arity} columns.");
        }

    }

}

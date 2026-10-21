using System;
using System.Collections.Generic;

namespace Eggnog
{

    /// <summary>
    /// What a rule callback (<see cref="RuleBuilder.ThenCall"/>) sees of one match and may do with it: egglog's
    /// <c>WriteState</c>, which a <c>rust_rule</c>'s action receives. It reads the match's variables and writes as a head
    /// does: rows, function values, terms, unions, deletions and subsumptions, staged with the iteration's other writes.
    /// It reads no table: egglog's write state cannot, since a semi-naive rule would not run again when the row it read
    /// changed; <see cref="FullRuleCallbackContext"/> can, in a naive rule. Valid only during the call.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Values are of their columns' types, a class a sort value usable in the scope the rule runs in. A write is checked as
    /// a head's is, when it is made rather than before the run, since the callback decides what it writes: a function whose
    /// merge is not a lattice is written only at the root, a deletion only where the scope may delete, a subsumption only
    /// of a subsumable table, and no analysis table and no scoped view is written. A refused write, or an exception the
    /// callback throws, fails the rule with a <see cref="RuleEvaluationException"/>, as egglog's callback returning
    /// <c>None</c> panics; a refusal fails the rule even when the callback catches it.
    /// </para>
    /// <para>
    /// Callbacks run on the engine's threads, in parallel with each other, and must be thread-safe; one cannot call into a
    /// graph.
    /// </para>
    /// </remarks>
    public class RuleCallbackContext
    {

        readonly Relations.ExecutionState state;
        readonly uint[] values;
        readonly ColumnType[] types;
        readonly IReadOnlyDictionary<Term, int> variables;
        Exception? refusal;
        bool done;

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="call">The call site.</param>
        /// <param name="state">The engine's state for the call.</param>
        /// <param name="scope">The scope the rule runs in.</param>
        /// <param name="values">The variables' cells.</param>
        internal RuleCallbackContext(RuleCallbackCall call, Relations.ExecutionState state, Scope scope, ReadOnlySpan<uint> values)
        {
            Call = call;
            this.state = state;
            Scope = scope;
            this.values = values.ToArray();
            types = call.Types;
            variables = call.Variables;
        }

        /// <summary>
        /// Gets the call site.
        /// </summary>
        internal RuleCallbackCall Call { get; }

        /// <summary>
        /// Gets the scope the rule runs in.
        /// </summary>
        internal Scope Scope { get; }

        /// <summary>
        /// Gets the engine's state, checking that the call has not returned.
        /// </summary>
        internal Relations.ExecutionState State
        {
            get
            {
                if (done)
                    throw Refuse(new InvalidOperationException("A rule callback's context is valid only during the call."));

                return state;
            }
        }

        /// <summary>
        /// Gets the value of a variable of the rule in this match: egglog's <c>value_to_base</c> of its value. A variable
        /// the body binds, or a <see cref="RuleBuilder.Make"/> variable made before the callback.
        /// </summary>
        /// <typeparam name="T">The variable's type.</typeparam>
        /// <param name="variable">The variable.</param>
        /// <returns>The value.</returns>
        /// <exception cref="ArgumentException">The variable is not one the callback sees.</exception>
        public T Get<T>(Var<T> variable)
        {
            ArgumentNullException.ThrowIfNull(variable);
            _ = State;
            if (variables.TryGetValue(variable, out var index) == false)
                throw Refuse(new ArgumentException($"Variable '{variable.Name}' is not bound before the callback.", nameof(variable)));

            return (T)types[index].DecodeObject(values[index], Scope);
        }

        /// <summary>
        /// Inserts a row of a relation: egglog's <c>insert</c> of the row.
        /// </summary>
        /// <param name="relation">The relation, of the rule's graph.</param>
        /// <param name="keys">One value per column.</param>
        /// <exception cref="ArgumentException">The relation, or a value, is not one this rule may write.</exception>
        public void Insert(RelationHandle relation, params ReadOnlySpan<object> keys)
        {
            var row = Row(relation, keys, 1, writes: true);
            row[^1] = Scope.Graph.Unit.Unit;
            Call.Action(relation).Insert(State, row);
        }

        /// <summary>
        /// Sets a function's value at a key: egglog's <c>insert</c> of the row; two values for one key merge as the
        /// function merges them.
        /// </summary>
        /// <param name="function">The function, of the rule's graph.</param>
        /// <param name="keys">One value per key column.</param>
        /// <param name="value">The value, of the output's type.</param>
        /// <exception cref="ArgumentException">The function, or a value, is not one this rule may write.</exception>
        public void Set(FunctionHandle function, ReadOnlySpan<object> keys, object value)
        {
            ArgumentNullException.ThrowIfNull(value);
            var row = Row(function, keys, 1, writes: true);
            row[^1] = Encode(function.Columns[^1], value, nameof(value));
            Call.Action(function).Insert(State, row);
        }

        /// <summary>
        /// Gets the class of a constructor term, making it when it is new: egglog's lookup of a constructor in an action,
        /// which mints a fresh class on a miss.
        /// </summary>
        /// <typeparam name="TSort">The constructor's sort.</typeparam>
        /// <param name="constructor">The constructor, of the rule's graph.</param>
        /// <param name="keys">One value per child.</param>
        /// <returns>The term's class.</returns>
        /// <exception cref="ArgumentException">The constructor, or a value, is not one this rule may write.</exception>
        public TSort Make<TSort>(ConstructorHandle constructor, params ReadOnlySpan<object> keys) where TSort : struct, ISort<TSort>
        {
            var key = Row(constructor, keys, 0, writes: true);
            if (constructor.Output!.ClrType != typeof(TSort))
                throw Refuse(new ArgumentException(
                    $"Constructor '{constructor.Name}' makes {constructor.Output.Name}, not {typeof(TSort).Name}.", nameof(constructor)));

            Call.Action(constructor).TryLookupOrInsert(State, key, out var cell);
            return (TSort)constructor.Output.DecodeObject(cell, Scope);
        }

        /// <summary>
        /// Unions two classes: egglog's <c>union</c>.
        /// </summary>
        /// <typeparam name="TSort">The sort.</typeparam>
        /// <param name="a">One class.</param>
        /// <param name="b">The other.</param>
        /// <exception cref="ArgumentException">A class is not usable in the scope the rule runs in.</exception>
        public void Union<TSort>(TSort a, TSort b) where TSort : struct, ISort<TSort>
        {
            var type = Scope.Graph.Values.TypeOf<TSort>();
            new Relations.UnionAction(Scope.Graph.Engine).Union(State, Encode(type, a, nameof(a)), Encode(type, b, nameof(b)));
        }

        /// <summary>
        /// Deletes the row of a key: egglog's <c>remove</c>. Only where the scope may delete, as a rule that deletes.
        /// </summary>
        /// <param name="table">The relation, function or constructor, of the rule's graph.</param>
        /// <param name="keys">One value per key column.</param>
        /// <exception cref="ArgumentException">The table, or a value, is not one this rule may write.</exception>
        /// <exception cref="InvalidOperationException">The scope may not delete.</exception>
        public void Remove(TableHandle table, params ReadOnlySpan<object> keys)
        {
            var key = Row(table, keys, 0, writes: true);
            if (Scope.Engine.RetractionRefusal is { } why)
                throw Refuse(new InvalidOperationException($"'{table.Name}' cannot lose a row in '{Scope.Path}': {why}."));

            Call.Action(table).Remove(State, key);
            Scope.Runtime.NoteCallbackDeletion(table);
        }

        /// <summary>
        /// Subsumes the row of a key: egglog's <c>subsume</c>. The table must be subsumable, and hold the row.
        /// </summary>
        /// <param name="table">The relation or constructor, of the rule's graph, defined
        /// <see cref="TableOptions.Subsumable"/>.</param>
        /// <param name="keys">One value per key column.</param>
        /// <exception cref="ArgumentException">The table, or a value, is not one this rule may write, or the table is not
        /// subsumable.</exception>
        /// <exception cref="InvalidOperationException">The table holds no row for the key.</exception>
        public void Subsume(TableHandle table, params ReadOnlySpan<object> keys)
        {
            var key = Row(table, keys, 0, writes: true);
            if (table.Subsumable == false)
                throw Refuse(new ArgumentException($"'{table.Name}' is not subsumable.", nameof(table)));

            Call.Action(table).Subsume(State, key);
        }

        /// <summary>
        /// Gets the first write or read the context refused, which fails the rule whether or not the callback caught it.
        /// </summary>
        internal Exception? Refusal => refusal;

        /// <summary>
        /// Ends the call: the context answers nothing after.
        /// </summary>
        internal void End() => done = true;

        /// <summary>
        /// Records a refusal, the first one only, and returns it to throw.
        /// </summary>
        /// <param name="exception">The refusal.</param>
        /// <returns>The refusal.</returns>
        internal Exception Refuse(Exception exception)
        {
            refusal ??= exception;
            return exception;
        }

        /// <summary>
        /// Encodes the keys of a row of <paramref name="table"/>, with <paramref name="extra"/> free cells after them,
        /// checking that this rule may use the table.
        /// </summary>
        /// <exception cref="ArgumentException">The table belongs to another graph, is a scoped view, or may not be written
        /// here, or a value is not of its column's type or not usable here.</exception>
        internal uint[] Row(TableHandle table, ReadOnlySpan<object> keys, int extra, bool writes)
        {
            CheckTable(table, writes);
            var columns = table.KeyArray;
            if (keys.Length != columns.Length)
                throw Refuse(new ArgumentException($"'{table.Name}' takes {columns.Length} keys, not {keys.Length}.", nameof(keys)));

            var row = new uint[columns.Length + extra];
            for (var i = 0; i < columns.Length; i++)
            {
                var key = keys[i] ?? throw Refuse(new ArgumentNullException(nameof(keys), $"Key {i} is null."));
                row[i] = Encode(columns[i], key, nameof(keys));
            }

            return row;
        }

        /// <summary>
        /// Checks that this rule may read, or with <paramref name="writes"/> write, <paramref name="table"/>.
        /// </summary>
        /// <exception cref="ArgumentException">The table belongs to another graph, is a scoped view, or may not be written
        /// here.</exception>
        internal void CheckTable(TableHandle table, bool writes)
        {
            ArgumentNullException.ThrowIfNull(table);
            _ = State;
            if (ReferenceEquals(table.Graph, Scope.Graph) == false || table.Index < 0)
                throw Refuse(new ArgumentException($"Table '{table.Name}' belongs to another graph.", nameof(table)));

            if (table.ReadScope is not null)
                throw Refuse(new ArgumentException(
                    $"'{table.Name}@{table.ReadScope.Name}' is a scoped view, which a rule callback does not use.", nameof(table)));

            if (writes)
            {
                if (table.Role == TableRole.Analysis)
                    throw Refuse(new ArgumentException(
                        $"A rule callback does not write analysis table '{table.Name}': analyses depend only on terms, which a callback "
                        + "cannot show.",
                        nameof(table)));

                if (Scope.IsRoot == false && table is FunctionHandle { Merge: not null })
                    throw Refuse(new ArgumentException(
                        $"'{table.Name}' merges with a function, not a lattice, and only the root writes it.", nameof(table)));
            }
        }

        /// <summary>
        /// Encodes a value of a column, a class checked usable in the scope.
        /// </summary>
        /// <exception cref="ArgumentException">The value is not of the column's type, or is a class not usable here.</exception>
        internal uint Encode(ColumnType type, object value, string parameter)
        {
            if (type.ClrType.IsInstanceOfType(value) == false)
                throw Refuse(new ArgumentException($"A {ValueStore.DisplayName(value.GetType())} is not a {type.Name}.", parameter));

            // Canonical through the state, not the scope, whose find enters a thread domain a parallel run's worker
            // does not hold: a row of a stale id, such as a class the host kept, would join as no class until a rebuild.
            if (type.Kind == ColumnKind.Sort)
            {
                uint cell;
                try
                {
                    cell = ClassChecks.Require(Scope, type.ClassOf(value), parameter);
                }
                catch (ArgumentException refused)
                {
                    throw Refuse(refused);
                }

                return State.Canonicalize(cell);
            }

            return type.EncodeObject(value);
        }

    }

}

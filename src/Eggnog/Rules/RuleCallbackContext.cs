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
    public class RuleCallbackContext : ITableWriter
    {

        readonly uint[] values;
        readonly ColumnType[] types;
        readonly IReadOnlyDictionary<Term, int> variables;

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="call">The call site.</param>
        /// <param name="state">The engine's state for the call.</param>
        /// <param name="scope">The scope the rule runs in.</param>
        /// <param name="values">The variables' cells.</param>
        internal RuleCallbackContext(RuleCallbackCall call, Relations.ExecutionState state, Scope scope, ReadOnlySpan<uint> values)
        {
            Tables = new EngineTables(scope, state, call.Actions, "A rule callback");
            this.values = values.ToArray();
            types = call.Types;
            variables = call.Variables;
        }

        /// <summary>
        /// Gets the tables the callback reads and writes, over the call's state.
        /// </summary>
        internal EngineTables Tables { get; }

        /// <summary>
        /// Gets the scope the rule runs in.
        /// </summary>
        internal Scope Scope => Tables.Scope;

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
            _ = Tables.State;
            if (variables.TryGetValue(variable, out var index) == false)
                throw Tables.Refuse(new ArgumentException($"Variable '{variable.Name}' is not bound before the callback.", nameof(variable)));

            return (T)types[index].DecodeObject(values[index], Scope);
        }

        /// <inheritdoc />
        public void Insert(RelationHandle relation, params ReadOnlySpan<object> keys) => Tables.Insert(relation, keys);

        /// <inheritdoc />
        public void Set(FunctionHandle function, ReadOnlySpan<object> keys, object value) => Tables.Set(function, keys, value);

        /// <inheritdoc />
        public TSort Make<TSort>(ConstructorHandle constructor, params ReadOnlySpan<object> keys) where TSort : struct, ISort<TSort> =>
            Tables.Make<TSort>(constructor, keys);

        /// <inheritdoc />
        public void Union<TSort>(TSort a, TSort b) where TSort : struct, ISort<TSort> => Tables.Union(a, b);

        /// <inheritdoc />
        /// <remarks>Only where the scope may delete, as a rule that deletes.</remarks>
        public void Remove(TableHandle table, params ReadOnlySpan<object> keys) => Tables.Remove(table, keys);

        /// <inheritdoc />
        public void Subsume(TableHandle table, params ReadOnlySpan<object> keys) => Tables.Subsume(table, keys);

        /// <summary>
        /// Gets the first write or read the context refused, which fails the rule whether or not the callback caught it.
        /// </summary>
        internal Exception? Refusal => Tables.Refusal;

        /// <summary>
        /// Ends the call: the context answers nothing after.
        /// </summary>
        internal void End() => Tables.End();

    }

}

using System;

namespace Eggnog
{

    /// <summary>
    /// What a rule callback of <see cref="RuleBuilder.ThenCallFull"/> may do: egglog's <c>FullState</c>, which a
    /// <c>rust_rule_full</c>'s action receives. Besides writing, as <see cref="RuleCallbackContext"/>, it reads tables,
    /// which is why its rule is naive: matched in full every run, as egglog marks it, so that a match is found again when
    /// a row it read changes.
    /// </summary>
    public sealed class FullRuleCallbackContext : RuleCallbackContext
    {

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="call">The call site.</param>
        /// <param name="state">The engine's state for the call.</param>
        /// <param name="scope">The scope the rule runs in.</param>
        /// <param name="values">The variables' cells.</param>
        internal FullRuleCallbackContext(RuleCallbackCall call, Relations.ExecutionState state, Scope scope, ReadOnlySpan<uint> values)
            : base(call, state, scope, values)
        {

        }

        /// <summary>
        /// Reads a function's value, or a constructor's class, at a key, without inserting: egglog's <c>lookup</c>.
        /// </summary>
        /// <typeparam name="T">The output's type.</typeparam>
        /// <param name="table">The function or constructor, of the rule's graph.</param>
        /// <param name="keys">One value per key column.</param>
        /// <param name="value">The value, when the key has a row.</param>
        /// <returns>Whether the key has a row.</returns>
        /// <exception cref="ArgumentException">The table is a relation or not of the rule's graph, or a value is not of its
        /// column's type.</exception>
        public bool TryLookup<T>(TableHandle table, object[] keys, out T value)
        {
            value = default!;
            var key = Row(table, keys, 0, writes: false);
            if (table.IsRelation)
                throw Refuse(new ArgumentException(
                    $"'{table.Name}' is a relation, which has no value to read; use Contains.", nameof(table)));

            if (table.Columns[^1].ClrType != typeof(T))
                throw Refuse(new ArgumentException(
                    $"'{table.Name}' outputs {table.Columns[^1].Name}, not {typeof(T).Name}.", nameof(table)));

            if (Call.Action(table).TryLookup(State, key, out var cell) == false)
                return false;

            var output = table.Columns[^1];
            value = (T)output.DecodeObject(output.InvolvesClasses ? State.Canonicalize(cell) : cell, Scope);
            return true;
        }

        /// <summary>
        /// Gets whether a relation holds a row.
        /// </summary>
        /// <param name="relation">The relation, of the rule's graph.</param>
        /// <param name="keys">One value per column.</param>
        /// <returns>Whether the row is there.</returns>
        /// <exception cref="ArgumentException">The relation is not of the rule's graph, or a value is not of its column's
        /// type.</exception>
        public bool Contains(RelationHandle relation, params object[] keys) =>
            Call.Action(relation).TryLookup(State, Row(relation, keys, 0, writes: false), out _);

        /// <summary>
        /// Gets the number of rows of a table the scope sees: egglog's <c>table_size</c>.
        /// </summary>
        /// <param name="table">The table, of the rule's graph.</param>
        /// <returns>The number of rows.</returns>
        /// <exception cref="ArgumentException">The table is not of the rule's graph.</exception>
        public long TableSize(TableHandle table)
        {
            CheckTable(table, writes: false);
            return Call.Action(table).RowCount(State);
        }

    }

}

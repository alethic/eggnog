using System;

namespace Eggnog
{

    /// <summary>
    /// What a rule callback of <see cref="RuleBuilder.ThenCallFull"/> may do: egglog's <c>FullState</c>, which a
    /// <c>rust_rule_full</c>'s action receives. Besides writing, as <see cref="RuleCallbackContext"/>, it reads tables,
    /// which is why its rule is naive: matched in full every run, as egglog marks it, so that a match is found again when
    /// a row it read changes.
    /// </summary>
    public sealed class FullRuleCallbackContext : RuleCallbackContext, ITables
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

        /// <inheritdoc />
        public bool TryLookup<T>(TableHandle table, ReadOnlySpan<object> keys, out T value) => Tables.TryLookup(table, keys, out value);

        /// <inheritdoc />
        public bool Contains(RelationHandle relation, params ReadOnlySpan<object> keys) => Tables.Contains(relation, keys);

        /// <inheritdoc />
        /// <remarks>Of the scope the rule runs in.</remarks>
        public long TableSize(TableHandle table) => Tables.TableSize(table);

    }

}

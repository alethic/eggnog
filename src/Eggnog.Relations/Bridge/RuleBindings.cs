using System;
using System.Diagnostics;

namespace Eggnog.Relations
{

    /// <summary>
    /// The translation environment used while an e-graph rule is lowered onto a core rule: each rule variable's core
    /// entry, the core variable holding the run's timestamp, and the variables the left-hand side grounds. egglog-bridge's
    /// <c>Bindings</c> (egglog-bridge/src/rule.rs:849-872), named apart from the action interpreter's
    /// <see cref="Bindings"/>.
    /// </summary>
    /// <remarks>
    /// Every rule variable starts mapped to a core variable of its own (egglog's <c>query_state</c>,
    /// egglog-bridge/src/rule.rs:719-740); atoms bind those, and actions that define a variable remap it to the core
    /// variable their instruction defines. Lives for one lowering; not thread-safe. Cold path.
    /// </remarks>
    internal sealed class RuleBindings
    {

        readonly QueryEntry[] mapping;
        readonly FixedBitSet grounded;
        QueryEntry nextTimestamp;
        bool hasNextTimestamp;

        /// <summary>
        /// Initializes an environment for a rule with <paramref name="variableCount"/> variables, all unmapped.
        /// </summary>
        /// <param name="variableCount">The number of rule variables.</param>
        public RuleBindings(int variableCount)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(variableCount);
            mapping = new QueryEntry[variableCount];
            grounded = new FixedBitSet(Math.Max(variableCount, 1));
        }

        /// <summary>
        /// Gets the core variable holding the run's timestamp, which every row the rule writes is stamped with. egglog's
        /// <c>next_ts</c>.
        /// </summary>
        /// <exception cref="InvalidOperationException">The right-hand side has not started: the timestamp is read by
        /// the first instruction of the right-hand side.</exception>
        public QueryEntry NextTimestamp => hasNextTimestamp ? nextTimestamp : throw new InvalidOperationException("The timestamp variable should only be used in the right-hand side of the rule.");

        /// <summary>
        /// Sets the core variable holding the run's timestamp.
        /// </summary>
        /// <param name="variable">The variable the rule's <see cref="InstrKind.ReadCounter"/> defines.</param>
        public void SetNextTimestamp(Variable variable)
        {
            nextTimestamp = QueryEntry.Var(variable);
            hasNextTimestamp = true;
        }

        /// <summary>
        /// Maps <paramref name="variable"/> to the core variable <paramref name="target"/>.
        /// </summary>
        /// <param name="variable">The rule variable.</param>
        /// <param name="target">The core variable.</param>
        public void Bind(VariableId variable, Variable target) => mapping[variable.Index] = QueryEntry.Var(target);

        /// <summary>
        /// Gets whether an atom or a left-hand-side primitive has bound <paramref name="variable"/>.
        /// </summary>
        /// <param name="variable">The rule variable.</param>
        /// <returns><c>true</c> if grounded.</returns>
        public bool IsGrounded(VariableId variable) => grounded.Contains(variable.Index);

        /// <summary>
        /// Records that an atom or a left-hand-side primitive binds <paramref name="variable"/>.
        /// </summary>
        /// <param name="variable">The rule variable.</param>
        public void Ground(VariableId variable) => grounded.Insert(variable.Index);

        /// <summary>
        /// Translates <paramref name="entry"/> into a core entry. egglog's <c>convert</c>.
        /// </summary>
        /// <param name="entry">The entry.</param>
        /// <returns>The variable's current core entry, or the constant.</returns>
        public QueryEntry Convert(RuleEntry entry)
        {
            if (entry.IsVariable == false)
                return QueryEntry.Const(entry.Constant);

            var mapped = mapping[entry.Variable.Id.Index];
            Debug.Assert(mapped.IsVariable, "every rule variable is mapped to a core variable before the rule is lowered");
            return mapped;
        }

        /// <summary>
        /// Translates every entry of <paramref name="entries"/>. egglog's <c>convert_all</c>.
        /// </summary>
        /// <param name="entries">The entries.</param>
        /// <returns>A new array of core entries.</returns>
        public QueryEntry[] ConvertAll(ReadOnlySpan<RuleEntry> entries)
        {
            var result = new QueryEntry[entries.Length];
            for (var i = 0; i < entries.Length; i++)
                result[i] = Convert(entries[i]);

            return result;
        }

    }

}

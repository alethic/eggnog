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
        TableId stopTable;
        ExternalFunctionId stopFunction;
        bool hasStop;

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
        /// Sets the contradiction early stop: every head insert into <paramref name="table"/>, the e-graph's
        /// contradiction function, is followed by a call to <paramref name="stop"/>
        /// (<see cref="StopOnContradictionFunction"/>, <see cref="TryGetStop"/>).
        /// </summary>
        /// <param name="table">The contradiction function's table.</param>
        /// <param name="stop">The early-stop function.</param>
        public void SetStopOnContradiction(TableId table, ExternalFunctionId stop)
        {
            stopTable = table;
            stopFunction = stop;
            hasStop = true;
        }

        /// <summary>
        /// Gets the early-stop function to call after a head insert into <paramref name="table"/>.
        /// </summary>
        /// <param name="table">The table inserted into.</param>
        /// <param name="stop">The early-stop function.</param>
        /// <returns><c>true</c> if <paramref name="table"/> is the contradiction function's table.</returns>
        public bool TryGetStop(TableId table, out ExternalFunctionId stop)
        {
            stop = stopFunction;
            return hasStop && table == stopTable;
        }

        /// <summary>
        /// Translates <paramref name="entry"/> into a core entry for a table column. egglog's <c>convert</c>.
        /// </summary>
        /// <remarks>
        /// A constant stays a plain constant: whether it is an e-class id is decided by the column it lands in, which
        /// the core reads from the table (<see cref="Database.GetIdColumns"/>), so a store-backed context runs a
        /// constant of an id column in its canonical form there and a value column's constant as written. A constant's
        /// <see cref="ColumnTy"/> is a diagnostic only, as in egglog, and is not relied on here. Operands without a
        /// table column go through <see cref="ConvertLoose"/>.
        /// </remarks>
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
        /// Translates <paramref name="entry"/> into a core entry for an operand that lands in no table column: an
        /// external function's argument, a comparison's operand, a lookup's default. A constant written as an e-class
        /// id (<see cref="ColumnTy.Id"/>) is flagged so (<see cref="QueryEntry.IdConst"/>), since no column says it is
        /// one: a store-backed context then runs it in its canonical form, as the table columns' constants are run.
        /// </summary>
        /// <param name="entry">The entry.</param>
        /// <returns>The variable's current core entry, or the constant, flagged when it is an id.</returns>
        public QueryEntry ConvertLoose(RuleEntry entry)
        {
            if (entry.IsVariable)
                return Convert(entry);

            return entry.ConstantType.IsId ? QueryEntry.IdConst(entry.Constant) : QueryEntry.Const(entry.Constant);
        }

        /// <summary>
        /// Translates every entry of <paramref name="entries"/> with <see cref="ConvertLoose"/>.
        /// </summary>
        /// <param name="entries">The entries.</param>
        /// <returns>A new array of core entries.</returns>
        public QueryEntry[] ConvertAllLoose(ReadOnlySpan<RuleEntry> entries)
        {
            var result = new QueryEntry[entries.Length];
            for (var i = 0; i < entries.Length; i++)
                result[i] = ConvertLoose(entries[i]);

            return result;
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

using System;
using System.Text;

namespace Eggnog.Relations
{

    /// <summary>
    /// One instruction of a rule's right-hand side: egglog's <c>Instr</c> (core-relations/src/action/mod.rs:885-989).
    /// <see cref="ExecutionState.RunInstrs(ReadOnlySpan{Instr}, Bindings)"/> runs a program of them over a batch of
    /// matches, one instruction at a time across every live lane.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A closed hierarchy of sealed classes, one per variant: <see cref="Kind"/> says which, so the interpreter
    /// dispatches with a <c>switch</c> plus <c>Unsafe.As</c>, never a virtual call. Instructions are immutable data
    /// (operand arrays are copied in and must not be modified through the properties), shared by every batch and
    /// thread that runs the rule, and complete enough for a compiler to turn a program into a delegate.
    /// </para>
    /// <para>
    /// Operands are <see cref="QueryEntry"/> values: a variable bound by the join or defined by an earlier instruction,
    /// or a constant. The wildcard is not an operand. Instructions that define a variable name it
    /// <c>Destination</c>; the rule builder gives each instruction a fresh one.
    /// </para>
    /// </remarks>
    internal abstract class Instr
    {

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="kind">The kind of the concrete instruction.</param>
        private protected Instr(InstrKind kind)
        {
            Kind = kind;
        }

        /// <summary>
        /// Gets the kind of instruction.
        /// </summary>
        public InstrKind Kind { get; }

        /// <summary>
        /// Copies an operand list, rejecting wildcards.
        /// </summary>
        /// <param name="entries">The operands.</param>
        /// <param name="parameter">The parameter name, for the exception.</param>
        /// <returns>The copy.</returns>
        private protected static QueryEntry[] CopyOperands(ReadOnlySpan<QueryEntry> entries, string parameter)
        {
            var copy = entries.ToArray();
            foreach (var entry in copy)
                CheckOperand(entry, parameter);

            return copy;
        }

        /// <summary>
        /// Rejects a wildcard operand.
        /// </summary>
        /// <param name="entry">The operand.</param>
        /// <param name="parameter">The parameter name, for the exception.</param>
        /// <returns>The operand.</returns>
        private protected static QueryEntry CheckOperand(QueryEntry entry, string parameter)
        {
            if (entry.IsWildcard)
                throw new ArgumentException("A wildcard cannot be an operand of an action.", parameter);
            if (entry.IsVariable && entry.Variable.Rep < 0)
                throw new ArgumentOutOfRangeException(parameter, entry.Variable, "Variables have nonnegative indexes.");

            return entry;
        }

        /// <summary>
        /// Rejects a destination that is not a query variable.
        /// </summary>
        /// <param name="variable">The destination.</param>
        /// <param name="parameter">The parameter name, for the exception.</param>
        /// <returns>The destination.</returns>
        private protected static Variable CheckDestination(Variable variable, string parameter)
        {
            if (variable.IsPlaceholder || variable.Rep < 0)
                throw new ArgumentOutOfRangeException(parameter, variable, "The destination must be a query variable.");

            return variable;
        }

        /// <summary>
        /// Rejects an operand list that reads <paramref name="destination"/>: egglog moves the destination's column out
        /// of the bindings (<c>take</c>) while it evaluates such a list, so reading it there panics.
        /// </summary>
        /// <param name="entries">The operands.</param>
        /// <param name="destination">The destination.</param>
        /// <param name="parameter">The parameter name, for the exception.</param>
        private protected static void CheckNotReading(QueryEntry[] entries, Variable destination, string parameter)
        {
            foreach (var entry in entries)
                if (entry.IsVariable && entry.Variable == destination)
                    throw new ArgumentException($"The operands cannot read the destination {destination}.", parameter);
        }

        /// <summary>
        /// Rejects a negative column.
        /// </summary>
        /// <param name="column">The column.</param>
        /// <param name="parameter">The parameter name, for the exception.</param>
        /// <returns>The column.</returns>
        private protected static ColumnId CheckColumn(ColumnId column, string parameter)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(column.Rep, parameter);
            return column;
        }

        /// <summary>
        /// Appends <c>[a, b, ...]</c>.
        /// </summary>
        /// <typeparam name="T">The element type.</typeparam>
        /// <param name="builder">The builder.</param>
        /// <param name="items">The items.</param>
        /// <returns>The builder.</returns>
        private protected static StringBuilder AppendList<T>(StringBuilder builder, T[] items)
        {
            builder.Append('[');
            for (var i = 0; i < items.Length; i++)
            {
                if (i > 0)
                    builder.Append(", ");

                builder.Append(items[i]);
            }

            return builder.Append(']');
        }

    }

}

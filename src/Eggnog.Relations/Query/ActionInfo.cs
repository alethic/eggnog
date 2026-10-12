using System;
using System.Text;

namespace Eggnog.Relations
{

    /// <summary>
    /// The right-hand side of a rule: its instruction program and the query variables the program reads. egglog's
    /// <c>ActionInfo</c> (core-relations/src/query.rs:43-47).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="UsedVariables"/> lists, in ascending order, the variables some instruction reads that no instruction
    /// defines; every one of them is bound by an atom of the query (<see cref="RuleBuilder.Build(string)"/> checks it).
    /// The join pushes exactly these columns into a <see cref="Bindings"/> batch after its first match
    /// (<see cref="Bindings.Push(ReadOnlySpan{uint}, ReadOnlySpan{Variable}, ReadOnlySpan{Variable})"/>), then
    /// <see cref="ExecutionState.RunInstrs(Instr[], Bindings)"/> runs <see cref="Instrs"/> over the batch.
    /// </para>
    /// <para>
    /// Immutable, so one instance is shared by the rule, its cached plan and every rule set the cached plan is added to,
    /// across threads and contexts, as egglog shares the instructions behind an <c>Arc</c>.
    /// </para>
    /// </remarks>
    internal sealed class ActionInfo
    {

        /// <summary>
        /// Initializes a new instance. The arrays are owned by the new instance.
        /// </summary>
        /// <param name="instrs">The instruction program.</param>
        /// <param name="usedVariables">The query variables the program reads, ascending.</param>
        public ActionInfo(Instr[] instrs, Variable[] usedVariables) :
            this(instrs, usedVariables, Array.Empty<ActionIdConstant>())
        {

        }

        /// <summary>
        /// Initializes a new instance with the program's constant ids (colored-contexts spec §9.8). The arrays are owned by
        /// the new instance.
        /// </summary>
        /// <param name="instrs">The instruction program.</param>
        /// <param name="usedVariables">The query variables the program reads, ascending.</param>
        /// <param name="idConstants">The constant ids among the program's operands, as written.</param>
        public ActionInfo(Instr[] instrs, Variable[] usedVariables, ActionIdConstant[] idConstants)
        {
            ArgumentNullException.ThrowIfNull(instrs);
            ArgumentNullException.ThrowIfNull(usedVariables);
            ArgumentNullException.ThrowIfNull(idConstants);
            Instrs = instrs;
            UsedVariables = usedVariables;
            IdConstants = idConstants;
        }

        /// <summary>
        /// Gets the instruction program. Callers must not modify the array.
        /// </summary>
        public Instr[] Instrs { get; }

        /// <summary>
        /// Gets the variables bound by the query that the program reads, in ascending order: egglog's <c>used_vars</c>.
        /// Callers must not modify the array.
        /// </summary>
        public Variable[] UsedVariables { get; }

        /// <summary>
        /// Gets the constant ids among the program's operands, as written (colored-contexts spec §9.8, X19): what a
        /// store-backed context canonicalizes per run (<see cref="CanonicalConstants"/>). A program that
        /// <see cref="ActionSubstituter"/> rewrote keeps the written list. Callers must not modify the array.
        /// </summary>
        public ActionIdConstant[] IdConstants { get; }

        /// <summary>
        /// Formats the used variables and the program, one instruction per line.
        /// </summary>
        /// <returns>The text.</returns>
        public override string ToString()
        {
            var b = new StringBuilder();
            b.Append("used [").Append(string.Join(", ", UsedVariables)).Append(']').AppendLine();
            foreach (var instr in Instrs)
                b.Append("  ").Append(instr).AppendLine();

            return b.ToString();
        }

    }

}

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// What a <see cref="ColorCommand"/> does.
    /// </summary>
    enum ColorCommandKind
    {

        /// <summary>
        /// Mints an id in the context (family A): <see cref="ColorCommand.Result"/> is its id slot.
        /// </summary>
        NewId,

        /// <summary>
        /// Adds a constructor term (family B): <see cref="ColorCommand.Cells"/> is its key (id slots, or the value of
        /// <c>num</c>), <see cref="ColorCommand.Result"/> the slot of its id.
        /// </summary>
        Term,

        /// <summary>
        /// Asserts a logical row: <see cref="ColorCommand.Cells"/> holds an id slot per id column and a value per value
        /// column.
        /// </summary>
        Row,

        /// <summary>
        /// Unions the ids of the two slots in <see cref="ColorCommand.Cells"/>.
        /// </summary>
        Union,

        /// <summary>
        /// Deletes a row of the table: the one at <see cref="ColorCommand.Arg"/> (modulo the count) among the context's
        /// rows in rendered order, or a missing key when the table is empty. Refused, and checked to be, where colors
        /// §6.7 does not allow it.
        /// </summary>
        Delete,

        /// <summary>
        /// Clears the table, with the same rule.
        /// </summary>
        Clear,

        /// <summary>
        /// Runs the rules of mask <see cref="ColorCommand.Arg"/> once (<see cref="EGraphContext.RunRules"/>).
        /// </summary>
        Run,

        /// <summary>
        /// Saturates the rules of mask <see cref="ColorCommand.Arg"/>: through <see cref="EGraphContext.Saturate"/> when
        /// <see cref="ColorCommand.Result"/> is 0, else by a loop of runs checked one by one.
        /// </summary>
        Saturate,

        /// <summary>
        /// Creates a child of the context: <see cref="ColorCommand.Result"/> is its context slot.
        /// </summary>
        Child,

        /// <summary>
        /// Freezes the context.
        /// </summary>
        Freeze,

        /// <summary>
        /// Disposes the context.
        /// </summary>
        Dispose,

        /// <summary>
        /// Rebuilds the context and checks its invariants.
        /// </summary>
        Rebuild,

        /// <summary>
        /// Compares every live context with the oracles.
        /// </summary>
        Check,

    }

}

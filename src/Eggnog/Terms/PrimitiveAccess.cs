namespace Eggnog
{

    /// <summary>
    /// What a primitive may do to the graph's tables, egglog's four kinds of primitive (src/exec_state.rs): each runs only
    /// where the call site's context allows it, as egglog's typechecker accepts a primitive only in the contexts it is
    /// valid in. A semi-naive rule's body is <see cref="Pure"/> and its head <see cref="Write"/>; a naive rule's body
    /// (<see cref="RuleBuilder.Naive"/>) is <see cref="Read"/> and its head <see cref="Full"/>; a merge expression is
    /// <see cref="Write"/>; a host write is <see cref="Full"/>; a host read, a query and a schedule's condition are
    /// <see cref="Read"/>.
    /// </summary>
    public enum PrimitiveAccess
    {

        /// <summary>
        /// Neither reads nor writes tables: egglog's <c>PurePrim</c>, valid everywhere.
        /// </summary>
        Pure,

        /// <summary>
        /// Reads tables (<see cref="ITableReader"/>): egglog's <c>ReadPrim</c>, valid where reads are, a naive rule's body
        /// and head, a host read and a host write. A semi-naive rule cannot read, since it would not run again when the row
        /// it read changed.
        /// </summary>
        Read,

        /// <summary>
        /// Writes tables (<see cref="ITableWriter"/>): egglog's <c>WritePrim</c>, valid where writes are, a rule's head, a
        /// merge expression and a host write.
        /// </summary>
        Write,

        /// <summary>
        /// Reads and writes tables (<see cref="ITables"/>): egglog's <c>FullPrim</c>, valid only where both are, a naive
        /// rule's head and a host write.
        /// </summary>
        Full,

    }

}

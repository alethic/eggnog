namespace Eggnog
{

    /// <summary>
    /// What a head operation of a program does.
    /// </summary>
    internal enum HeadOpKind
    {

        /// <summary>
        /// Inserts a relation row (the unit output is appended when lowering).
        /// </summary>
        Insert,

        /// <summary>
        /// Writes a function or constructor row: keys then the value or output, merged by the table's merge.
        /// </summary>
        Write,

        /// <summary>
        /// Looks a constructor term up, minting a fresh class for a new one, into a head register.
        /// </summary>
        GetOrAdd,

        /// <summary>
        /// Unions two classes.
        /// </summary>
        Union,

        /// <summary>
        /// Calls a primitive into a head register.
        /// </summary>
        Compute,

        /// <summary>
        /// Collects the operands as one match of a query or a split matcher: a <see cref="MatchCollector"/> call, which
        /// stages nothing.
        /// </summary>
        Collect,

    }

}

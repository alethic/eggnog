namespace Eggnog
{

    /// <summary>
    /// What a head operation of a program does.
    /// </summary>
    internal enum HeadOpKind
    {

        /// <summary>
        /// Inserts a relation row unless the table holds it: egglog's lookup-or-insert, the relation being a constructor
        /// there.
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
        /// Subsumes the row of a key in a subsumable relation or constructor, inserting it subsumed when it is missing:
        /// egglog's <c>subsume</c>.
        /// </summary>
        Subsume,

        /// <summary>
        /// Deletes the row of a key, if there is one: egglog's <c>delete</c>. The table's merge deletes before it
        /// inserts, so a row the same iteration also stages stays; a relation fact or constructor term the table already
        /// holds stages nothing (<see cref="Insert"/> and constructor terms look the row up), so its deletion wins.
        /// </summary>
        Delete,

        /// <summary>
        /// Calls a primitive into a head register.
        /// </summary>
        Compute,

        /// <summary>
        /// Collects the operands as one match of a query or a split matcher: a <see cref="MatchCollector"/> call, which
        /// stages nothing.
        /// </summary>
        Collect,

        /// <summary>
        /// A rule callback's call, with the variables it sees as inputs (<see cref="HeadOp.Callback"/>).
        /// </summary>
        Callback,

    }

}

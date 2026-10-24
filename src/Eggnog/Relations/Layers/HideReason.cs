namespace Eggnog.Relations
{

    /// <summary>
    /// Why a child context hid an ancestor's row: every hide carries the information of the row it hides somewhere the
    /// context still sees, and the reason says where. Recorded per hide in DEBUG builds, for the invariant checker.
    /// </summary>
    internal enum HideReason : byte
    {

        /// <summary>
        /// The row mentions an id that stopped being canonical in the context; a canonical copy was staged with it (by
        /// the catch-up of an ancestor append that names such an id, or by a rebuild pass's hide-and-copy of the
        /// ancestor rows that name the context's own new losers).
        /// </summary>
        Mentions = 1,

        /// <summary>
        /// The context's own layer now holds the row's key: a merge with the row produced a new own row, or a catch-up
        /// copied an ancestor append whose key the own layer already held.
        /// </summary>
        KeyHeld,

        /// <summary>
        /// The context deleted the row's key (<c>DeleteLayered</c>); legal only under a frozen parent.
        /// </summary>
        Retracted,

        /// <summary>
        /// The row was appended to an ancestor below the context's semi-naive floor, and a copy stamped with the
        /// catch-up epoch replaces it.
        /// </summary>
        Restamped,

        /// <summary>
        /// The row names a container the context's container pass changed in place, and every id it names is canonical
        /// in the context: a copy stamped with the pass's epoch replaces it, so the context's semi-naive evaluation
        /// sees the row's new meaning as new (<see cref="ContextTables.RebuildPass"/>).
        /// </summary>
        Refreshed,

    }

}

namespace Eggnog.Relations
{

    /// <summary>
    /// Why a child context hid an ancestor's row (colored-contexts spec §6.3, I14): every hide carries the information of
    /// the row it hides somewhere the context still sees, and the reason says where. Recorded per hide in DEBUG builds,
    /// for the invariant checker.
    /// </summary>
    internal enum HideReason : byte
    {

        /// <summary>
        /// The row mentions an id that stopped being canonical in the context; a canonical copy was staged with it
        /// (catch-up branch (a) and rebuild step 4, §7.2-§7.3).
        /// </summary>
        Mentions = 1,

        /// <summary>
        /// The context's own layer now holds the row's key: a merge with the row produced a new own row (§6.6), or a
        /// catch-up copied an ancestor append whose key the own layer already held (§7.2 branch (b)).
        /// </summary>
        KeyHeld,

        /// <summary>
        /// The context deleted the row's key (§6.6 <c>DeleteLayered</c>); legal only under a frozen parent (R1).
        /// </summary>
        Retracted,

        /// <summary>
        /// The row was appended to an ancestor below the context's semi-naive floor, and a copy stamped with the catch-up
        /// epoch replaces it (§7.2 branch (c′)).
        /// </summary>
        Restamped,

    }

}

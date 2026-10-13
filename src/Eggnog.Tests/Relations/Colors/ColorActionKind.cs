namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// What a <see cref="ColorAction"/> does.
    /// </summary>
    enum ColorActionKind
    {

        /// <summary>
        /// Writes a logical row (egglog's <c>set</c>): a relation's keys, a lattice row, or a constructor row with its
        /// output (family A), merged by the table's merge.
        /// </summary>
        Set,

        /// <summary>
        /// Unions two ids.
        /// </summary>
        Union,

        /// <summary>
        /// Looks a constructor term up, minting a fresh id for a missing key (egglog's <c>lookup</c> on a constructor),
        /// and defines a variable with its id: family B's minting. Query-determined, so the rule may inherit.
        /// </summary>
        Mint,

        /// <summary>
        /// Calls <see cref="ColorSchema.LowOr"/>, which reads <c>low</c> in the running context, and defines a variable
        /// with the value: a table read in an action (the history family), so the rule never inherits (X41).
        /// </summary>
        Read,

    }

}

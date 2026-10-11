namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// What a seed of a bridge differential program does: a command run outside the rules, as a top-level egglog
    /// command is.
    /// </summary>
    enum BridgeDiffSeedKind
    {

        /// <summary>
        /// Adds a term of a constructor (<see cref="EGraph.AddTerm"/>) and names its class.
        /// </summary>
        Term,

        /// <summary>
        /// Adds a row of a function (<see cref="EGraph.AddValues"/>), merged with the function's merge; or, for a relation
        /// fact written without an output, egglog's <c>(R a b)</c>, adds the key as a term of the relation
        /// (<see cref="BridgeDiffSeed.IsInsertion"/>).
        /// </summary>
        Set,

        /// <summary>
        /// Unions the classes of two earlier terms and flushes.
        /// </summary>
        Union,

        /// <summary>
        /// Adds many leaf terms at once: rows <c>F(k) = fresh id</c> of a constructor over one integer, for a range of
        /// keys, in one <see cref="EGraph.AddValues"/> call, so tables cross the engine's size thresholds (incremental
        /// rebuilds, parallel operations).
        /// </summary>
        Bulk,

    }

}

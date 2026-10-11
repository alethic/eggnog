namespace Eggnog.Relations
{

    /// <summary>
    /// The current sizes <see cref="StageOrdering"/> reads while reordering stages: the row count of each atom's current
    /// row set (egglog's <c>binding_info.subsets[atom].size()</c>) and the key count of each materialization
    /// (<c>binding_info.materializations[mat].len()</c>), as in egglog's <c>estimate_size</c>
    /// (core-relations/src/free_join/execute.rs:2266-2276).
    /// </summary>
    /// <remarks>
    /// Implemented by a struct and passed as a type argument, so the executor's calls are specialized and inlined; the
    /// sort runs at the start of every block and again at some depths of the search.
    /// </remarks>
    internal interface IStageSizeSource
    {

        /// <summary>
        /// Gets the number of rows in the current row set of <paramref name="atom"/>.
        /// </summary>
        /// <param name="atom">The atom.</param>
        /// <returns>The size.</returns>
        int GetAtomSize(AtomId atom);

        /// <summary>
        /// Gets the number of keys of materialization <paramref name="mat"/>.
        /// </summary>
        /// <param name="mat">The materialization.</param>
        /// <returns>The size.</returns>
        int GetMaterializationSize(MatId mat);

    }

}

namespace Eggnog.Relations
{

    /// <summary>
    /// What runs inside a case: the in-case schedule of a split. The driver runs it once per case with nested splits
    /// disabled (phase A), lifts, then again with nested splits enabled (phase B), which recurses into
    /// <see cref="SplitDriver.Step"/> at the next depth. What the cases derive is thus lifted before any case splits
    /// further.
    /// </summary>
    /// <remarks>
    /// Called without the scope's thread domain held, so the schedule may create children of the case (nested splits).
    /// Exceptions propagate out of <see cref="SplitDriver.Step"/>; the records opened stay for
    /// <see cref="SplitDriver.DisposeRecords"/>.
    /// </remarks>
    internal interface ICaseSchedule
    {

        /// <summary>
        /// Runs the in-case work in <paramref name="case"/>.
        /// </summary>
        /// <param name="case">The case.</param>
        /// <param name="depth">The case's split depth: its scope's depth plus one in phase B, the scope's depth in phase A.</param>
        /// <param name="allowSplits">Whether nested splits may be taken (phase B).</param>
        /// <returns>Whether the case changed.</returns>
        bool Run(EGraphContext @case, int depth, bool allowSplits);

    }

}

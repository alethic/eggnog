namespace Eggnog
{

    /// <summary>
    /// Where a host batch stood at a point of its planning, which it can go back to (<c>HostBatch.UndoTo</c>): the start
    /// of a fact, or of one write or deletion a primitive of the fact held. The lengths of the batch's lists, with
    /// the fact's bounds in them as they were then, since a deletion of the fact moves those bounds when it cancels what
    /// an earlier fact wrote.
    /// </summary>
    /// <param name="Terms">The number of new terms planned.</param>
    /// <param name="FactTerms">The number of new terms planned before the fact.</param>
    /// <param name="Rows">The number of rows planned.</param>
    /// <param name="FactRows">The number of rows planned before the fact.</param>
    /// <param name="Subsumes">The number of subsumed rows planned.</param>
    /// <param name="FactSubsumes">The number of subsumed rows planned before the fact.</param>
    /// <param name="Unions">The number of unions planned.</param>
    /// <param name="Removes">The number of deletions planned.</param>
    /// <param name="Merges">The number of merges of classes noted.</param>
    /// <param name="MergeRows">The number of rows of the congruence check.</param>
    /// <param name="ClassOutputs">The number of pairs of meeting rows the check's replay compares.</param>
    /// <param name="MergeCheckDecided">Whether the congruence check was decided.</param>
    /// <param name="Undo">The number of entries of the log that puts back what the fact changed.</param>
    internal readonly record struct FactMark(
        int Terms,
        int FactTerms,
        int Rows,
        int FactRows,
        int Subsumes,
        int FactSubsumes,
        int Unions,
        int Removes,
        int Merges,
        int MergeRows,
        int ClassOutputs,
        bool MergeCheckDecided,
        int Undo);

}

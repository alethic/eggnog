namespace Eggnog.Relations
{

    /// <summary>
    /// Counts of what a context's union-find did (colored-contexts spec §5.6): <see cref="Context.UnionFindStatistics"/>.
    /// </summary>
    /// <param name="OwnUnions">Effective unions merged in the context itself. At the root, its displaced count.</param>
    /// <param name="ImportedDisplacements">Parent displacements of ids that were canonical in the context, journaled
    /// while catching up.</param>
    /// <param name="RekeyedMembers">Canonical ids of the context's classes that lost while re-keying after a parent
    /// change, journaled while catching up.</param>
    /// <param name="ParentIdLosers">Own unions whose loser was an id visible to the parent and whose winner an id local
    /// to the context: the cost of smaller-id-wins when a parent that is not frozen lifts terms after cases created them
    /// (G2, L7).</param>
    internal readonly record struct UnionFindStatistics(long OwnUnions, long ImportedDisplacements, long RekeyedMembers, long ParentIdLosers);

}

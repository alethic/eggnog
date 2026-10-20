namespace Eggnog
{

    /// <summary>
    /// A table's row that <see cref="GraphSerializer"/> includes: egglog's entry of <c>all_calls</c> in
    /// <c>EGraph::serialize</c>.
    /// </summary>
    /// <param name="Table">The table.</param>
    /// <param name="Cells">The row's cells, the keys then the output; a class canonical in the scope.</param>
    /// <param name="Subsumed">Whether the row is subsumed.</param>
    /// <param name="ClassId">The id of the row's class: its output's.</param>
    /// <param name="NodeId">The id of the row's node.</param>
    internal readonly record struct SerializedCall(TableHandle Table, uint[] Cells, bool Subsumed, string ClassId, string NodeId);

}

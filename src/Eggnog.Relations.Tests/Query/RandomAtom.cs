namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// An atom of a random query in <see cref="RuleQueryPropertyTests"/>, as it is passed to
    /// <see cref="RuleQueryBuilder.AddAtom"/>.
    /// </summary>
    /// <param name="Table">The table.</param>
    /// <param name="Entries">One entry per column.</param>
    /// <param name="Constraints">The extra constraints.</param>
    sealed record RandomAtom(TableId Table, QueryEntry[] Entries, Constraint[] Constraints)
    {

        /// <inheritdoc />
        public override string ToString() => $"{Table}({string.Join(", ", (object[])System.Array.ConvertAll(Entries, e => (object)e))}){(Constraints.Length > 0 ? " where " + string.Join(", ", Constraints) : "")}";

    }

}

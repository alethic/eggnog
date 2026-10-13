namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// One assertion recorded by a <see cref="ReferenceContext"/>, in the order it was made: a union of two ids, a row,
    /// the retraction of the row with a key, the clearing of a table, or a row or union a rule run derived.
    /// </summary>
    /// <param name="Kind">What is asserted.</param>
    /// <param name="Table">The table, for rows, deletes and clears.</param>
    /// <param name="Row">The logical row of an insert or a derived row, or the key of a delete.</param>
    /// <param name="A">One id of a union or a derived union.</param>
    /// <param name="B">The other id of a union or a derived union.</param>
    readonly record struct ReferenceContextOp(ReferenceContextOpKind Kind, LogicalTable? Table, ulong[]? Row, ulong A, ulong B)
    {

        /// <inheritdoc />
        public override string ToString() => Kind switch
        {
            ReferenceContextOpKind.Union => $"union {A} {B}",
            ReferenceContextOpKind.Insert => $"{Table}({string.Join(",", Row!)})",
            ReferenceContextOpKind.Delete => $"delete {Table}({string.Join(",", Row!)})",
            ReferenceContextOpKind.Derived when Table is null => $"derived union {A} {B}",
            ReferenceContextOpKind.Derived => $"derived {Table}({string.Join(",", Row!)})",
            _ => $"clear {Table}",
        };

    }

}

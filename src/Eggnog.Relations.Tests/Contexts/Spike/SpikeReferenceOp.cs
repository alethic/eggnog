namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// One assertion recorded by a <see cref="SpikeReferenceContext"/>, in the order it was made: a union of two ids, a
    /// row, the retraction of the row with a key, or the clearing of a table (colored-contexts spec §17 M9.4).
    /// </summary>
    /// <param name="Kind">What is asserted.</param>
    /// <param name="Table">The table, for rows, deletes and clears.</param>
    /// <param name="Row">The logical row of an insert, or the key of a delete.</param>
    /// <param name="A">One id of a union.</param>
    /// <param name="B">The other id of a union.</param>
    readonly record struct SpikeReferenceOp(SpikeReferenceOpKind Kind, SpikeTable? Table, ulong[]? Row, ulong A, ulong B)
    {

        /// <inheritdoc />
        public override string ToString() => Kind switch
        {
            SpikeReferenceOpKind.Union => $"union {A} {B}",
            SpikeReferenceOpKind.Insert => $"{Table}({string.Join(",", Row!)})",
            SpikeReferenceOpKind.Delete => $"delete {Table}({string.Join(",", Row!)})",
            _ => $"clear {Table}",
        };

    }

}

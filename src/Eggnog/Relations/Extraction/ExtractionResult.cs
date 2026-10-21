namespace Eggnog.Relations
{

    /// <summary>
    /// An extracted term and its cost: egglog's <c>(C, TermId)</c> pair (src/extract.rs:573-617, 651-733).
    /// </summary>
    /// <param name="Cost">The term's cost under the extractor's <see cref="CostModel"/>.</param>
    /// <param name="Term">The term, in the <see cref="TermDag"/> it was extracted into.</param>
    internal readonly record struct ExtractionResult(ulong Cost, TermId Term);

}

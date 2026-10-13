namespace Eggnog.Relations
{

    /// <summary>
    /// A split a source asks for: its key, how many cases cover the scope, and what the source needs to assume each
    /// case (for a rule split, the matched tuple and the set's elements), handed back to
    /// <see cref="ISplitSource.Assume"/> unchanged.
    /// </summary>
    /// <param name="Key">What the split is about.</param>
    /// <param name="CaseCount">The number of cases; the driver skips a request with fewer than one or more than
    /// <see cref="SplitOptions.CaseLimit"/>.</param>
    /// <param name="Payload">The source's own data for <see cref="ISplitSource.Assume"/>.</param>
    internal readonly record struct SplitRequest(SplitKey Key, int CaseCount, object? Payload);

}

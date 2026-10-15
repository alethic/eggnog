namespace Eggnog.Tests.Differential.Splits
{

    /// <summary>
    /// One conjunct of a split-oracle query.
    /// </summary>
    /// <param name="Kind">What it says.</param>
    /// <param name="Path">The path it constrains.</param>
    /// <param name="Values">Its values: the allowed values, the excluded value, or the compared UUID.</param>
    public sealed record SplitOracleConjunct(SplitOracleConjunctKind Kind, string Path, string[] Values)
    {

        /// <summary>
        /// Renders the conjunct in predicate notation: <c>k0 in {a, b}</c>, <c>k0 != a</c> or <c>k0 = uuid u</c>.
        /// </summary>
        /// <returns>The rendering.</returns>
        public override string ToString() => Kind switch
        {
            SplitOracleConjunctKind.In => $"{Path} in {{{string.Join(", ", Values)}}}",
            SplitOracleConjunctKind.NotEqual => $"{Path} != {Values[0]}",
            _ => $"{Path} = uuid {Values[0]}",
        };

    }

}

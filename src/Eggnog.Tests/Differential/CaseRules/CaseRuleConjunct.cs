namespace Eggnog.Tests.Differential.CaseRules
{

    /// <summary>
    /// One conjunct of a case-rule oracle query.
    /// </summary>
    /// <param name="Kind">What it says.</param>
    /// <param name="Path">The path it constrains.</param>
    /// <param name="Values">Its values: the allowed values, or the excluded value; none for a numeric comparison.</param>
    public sealed record CaseRuleConjunct(CaseRuleConjunctKind Kind, string Path, string[] Values)
    {

        /// <summary>
        /// Renders the conjunct in predicate notation: <c>k0 in {a, b}</c>, <c>k0 != a</c> or <c>g00 &gt; 0</c>.
        /// </summary>
        /// <returns>The rendering.</returns>
        public override string ToString() => Kind switch
        {
            CaseRuleConjunctKind.In => $"{Path} in {{{string.Join(", ", Values)}}}",
            CaseRuleConjunctKind.NotEqual => $"{Path} != {Values[0]}",
            _ => $"{Path} > 0",
        };

    }

}

using System.Linq;

namespace Eggnog.Tests.Differential.Splits
{

    /// <summary>
    /// One conjunct of a split-oracle query.
    /// </summary>
    /// <param name="Kind">What it says.</param>
    /// <param name="Path">The path it constrains.</param>
    /// <param name="Values">Its values: the <c>IN</c> list, the excluded value, or the compared UUID.</param>
    public sealed record SplitOracleConjunct(SplitOracleConjunctKind Kind, string Path, string[] Values)
    {

        /// <summary>
        /// Renders the conjunct as SQL: <c>c.k0 IN ('a', 'b')</c>.
        /// </summary>
        /// <returns>The rendering.</returns>
        public override string ToString() => Kind switch
        {
            SplitOracleConjunctKind.In => $"c.{Path} IN ({string.Join(", ", Values.Select(v => "'" + v + "'"))})",
            SplitOracleConjunctKind.NotEqual => $"NOT (c.{Path} = '{Values[0]}')",
            _ => $"c.{Path} = uuid '{Values[0]}'",
        };

    }

}

using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Eggnog.Tests.Differential.Splits
{

    /// <summary>
    /// What one encoding of a split-oracle program proves in its query: whether the query is contradictory, and for
    /// each path the query mentions whether it is present, its stored forms, and its domain.
    /// </summary>
    public sealed class SplitOracleFacts
    {

        /// <summary>
        /// Gets or sets whether the query is contradictory.
        /// </summary>
        public bool Contradictory { get; set; }

        /// <summary>
        /// Gets the paths found present.
        /// </summary>
        public SortedSet<string> Present { get; } = new(System.StringComparer.Ordinal);

        /// <summary>
        /// Gets the stored forms found, as <c>path:form</c>.
        /// </summary>
        public SortedSet<string> Forms { get; } = new(System.StringComparer.Ordinal);

        /// <summary>
        /// Gets the domains found, by path.
        /// </summary>
        public SortedDictionary<string, ValueSet<string>> Domains { get; } = new(System.StringComparer.Ordinal);

        /// <summary>
        /// Renders the facts.
        /// </summary>
        /// <returns>The rendering.</returns>
        public override string ToString()
        {
            var text = new StringBuilder(Contradictory ? "contradictory; " : "");
            text.Append("present {").Append(string.Join(", ", Present)).Append("}; forms {").Append(string.Join(", ", Forms)).Append("}; dom ");
            text.Append(string.Join(", ", Domains.Select(d => $"{d.Key}={d.Value}")));
            return text.ToString();
        }

    }

}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Eggnog.Tests.Differential.CaseRules
{

    /// <summary>
    /// What the scoped encoding of a case-rule oracle program derives in a scope, its frozen schema or its query:
    /// whether the scope is contradictory, and for each of the program's paths whether it is present, its stored forms,
    /// its domain and its types.
    /// </summary>
    public sealed class CaseRuleFacts
    {

        /// <summary>
        /// Gets or sets whether the scope is contradictory.
        /// </summary>
        public bool Contradictory { get; set; }

        /// <summary>
        /// Gets the paths found present.
        /// </summary>
        public SortedSet<string> Present { get; } = new(StringComparer.Ordinal);

        /// <summary>
        /// Gets the stored forms found, as <c>path:form</c>.
        /// </summary>
        public SortedSet<string> Forms { get; } = new(StringComparer.Ordinal);

        /// <summary>
        /// Gets the domains found, by path.
        /// </summary>
        public SortedDictionary<string, Set<string>> Domains { get; } = new(StringComparer.Ordinal);

        /// <summary>
        /// Gets the type sets found, by path.
        /// </summary>
        public SortedDictionary<string, Set<string>> Types { get; } = new(StringComparer.Ordinal);

        /// <summary>
        /// Renders the facts.
        /// </summary>
        /// <returns>The rendering.</returns>
        public override string ToString()
        {
            var text = new StringBuilder(Contradictory ? "contradictory; " : "");
            text.Append("present {").Append(string.Join(", ", Present)).Append("}; forms {").Append(string.Join(", ", Forms)).Append("}; dom ");
            text.Append(string.Join(", ", Domains.Select(d => $"{d.Key}={d.Value}"))).Append("; types ");
            text.Append(string.Join(", ", Types.Select(t => $"{t.Key}={t.Value}")));
            return text.ToString();
        }

    }

}

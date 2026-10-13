using System.Collections.Generic;
using System.Linq;

namespace Eggnog.Tests.Differential.Splits
{

    /// <summary>
    /// One discriminated branch of a split-oracle schema (§8.2): under <c>discriminator = value</c>, its governed paths are
    /// present, use stored forms, or hold values from a domain.
    /// </summary>
    /// <param name="Discriminator">The discriminator's path.</param>
    /// <param name="Value">The branch's value.</param>
    /// <param name="Present">The paths it declares present.</param>
    /// <param name="Forms">The stored forms it declares, path and form.</param>
    /// <param name="Domains">The domains it declares, path and values.</param>
    public sealed record SplitOracleBranch(string Discriminator, string Value, string[] Present, (string Path, string Form)[] Forms,
        (string Path, string[] Values)[] Domains)
    {

        /// <summary>
        /// Gets the paths the branch mentions.
        /// </summary>
        public IEnumerable<string> Paths => Present.Concat(Forms.Select(f => f.Path)).Concat(Domains.Select(d => d.Path));

        /// <summary>
        /// Renders the branch: <c>k0 = 'a' =&gt; present(g00), form(g00, uuid-lower), dom(g01) = {x, y}</c>.
        /// </summary>
        /// <returns>The rendering.</returns>
        public override string ToString() =>
            $"{Discriminator} = '{Value}' => " + string.Join(", ",
                Present.Select(p => $"present({p})")
                    .Concat(Forms.Select(f => $"form({f.Path}, {f.Form})"))
                    .Concat(Domains.Select(d => $"dom({d.Path}) = {{{string.Join(", ", d.Values)}}}")));

    }

}

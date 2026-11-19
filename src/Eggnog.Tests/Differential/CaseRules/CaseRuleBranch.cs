using System.Linq;

namespace Eggnog.Tests.Differential.CaseRules
{

    /// <summary>
    /// One discriminated branch of a case-rule oracle schema: under <c>discriminator = value</c>, its governed paths are
    /// present, use stored forms, or hold values from a domain.
    /// </summary>
    /// <param name="Discriminator">The discriminator's path.</param>
    /// <param name="Value">The branch's value.</param>
    /// <param name="Present">The paths it declares present.</param>
    /// <param name="Forms">The stored forms it declares, path and form.</param>
    /// <param name="Domains">The domains it declares, path and values.</param>
    public sealed record CaseRuleBranch(string Discriminator, string Value, string[] Present, (string Path, string Form)[] Forms,
        (string Path, string[] Values)[] Domains)
    {

        /// <summary>
        /// Gets whether the branch declares anything.
        /// </summary>
        public bool IsEmpty => Present.Length == 0 && Forms.Length == 0 && Domains.Length == 0;

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

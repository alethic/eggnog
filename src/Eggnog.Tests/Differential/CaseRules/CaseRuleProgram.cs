using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Eggnog.Tests.Differential.CaseRules
{

    /// <summary>
    /// A random discriminated schema and a query over it (run by <c>CaseRuleModelOracleTests</c>): one or two
    /// discriminators with two to four values each, branches that declare presence, stored forms and domains of the
    /// paths each discriminator governs, what the schema itself holds of some of those paths (a number type or a
    /// domain, without presence), and a query of in-set conjuncts and disequalities on the discriminators, and of in-set
    /// conjuncts, disequalities and numeric comparisons on the governed paths.
    /// </summary>
    /// <remarks>
    /// Each discriminator governs its own paths, <c>g{i}0</c> and <c>g{i}1</c> for <c>k{i}</c>, so the values a model
    /// gives one discriminator and its paths never constrain another's.
    /// </remarks>
    /// <param name="Seed">The seed that made it.</param>
    /// <param name="Discriminators">Each discriminator's path and domain.</param>
    /// <param name="Branches">The branches.</param>
    /// <param name="Numbers">The governed paths the schema types as numbers.</param>
    /// <param name="Domains">The domains the schema declares of governed paths, path and values.</param>
    /// <param name="Conjuncts">The query's conjuncts.</param>
    public sealed record CaseRuleProgram(int Seed, (string Path, string[] Values)[] Discriminators, CaseRuleBranch[] Branches,
        string[] Numbers, (string Path, string[] Values)[] Domains, CaseRuleConjunct[] Conjuncts)
    {

        /// <summary>
        /// The stored forms a branch may declare.
        /// </summary>
        public static readonly string[] Forms = ["uuid-lower", "instant-ms"];

        /// <summary>
        /// The strings a governed path's declared domain holds.
        /// </summary>
        public static readonly string[] Strings = ["x", "y", "z"];

        /// <summary>
        /// Generates the program of a seed.
        /// </summary>
        /// <param name="seed">The seed.</param>
        /// <returns>The program.</returns>
        public static CaseRuleProgram Generate(int seed)
        {
            var random = new Random(seed);
            string[] values = ["a", "b", "c", "d"];
            var discriminators = new List<(string Path, string[] Values)>();
            var branches = new List<CaseRuleBranch>();
            var numbers = new List<string>();
            var domains = new List<(string Path, string[] Values)>();
            for (var i = 0; i < random.Next(1, 3); i++)
            {
                var path = "k" + i;
                var domain = values.OrderBy(_ => random.Next()).Take(random.Next(2, 5)).Order(StringComparer.Ordinal).ToArray();
                discriminators.Add((path, domain));
                foreach (var value in domain)
                {
                    var governed = Governed(path);
                    var present = governed.Where(_ => random.Next(3) > 0).ToArray();
                    var forms = governed.Where(_ => random.Next(2) == 0).Select(g => (g, Forms[random.Next(Forms.Length)])).ToArray();
                    var declared = governed.Where(_ => random.Next(2) == 0).Select(g => (g, Pick(random, Strings))).ToArray();
                    branches.Add(new CaseRuleBranch(path, value, present, forms, declared));
                }

                foreach (var governed in Governed(path))
                {
                    switch (random.Next(6))
                    {
                        case 0:
                            numbers.Add(governed);
                            break;
                        case 1:
                            domains.Add((governed, Pick(random, Strings)));
                            break;
                    }
                }
            }

            var conjuncts = new List<CaseRuleConjunct>();
            for (var n = random.Next(1, 5); conjuncts.Count < n;)
            {
                var (path, domain) = discriminators[random.Next(discriminators.Count)];
                var governed = Governed(path)[random.Next(2)];

                // Sometimes a value outside every domain, which narrows nothing a domain holds but must not hurt.
                var pool = random.Next(4) == 0 ? domain.Append("e").ToArray() : domain;
                var strings = random.Next(4) == 0 ? Strings.Append("e").ToArray() : Strings;
                conjuncts.Add(random.Next(6) switch
                {
                    0 or 1 => new CaseRuleConjunct(CaseRuleConjunctKind.In, path, Pick(random, pool)),
                    2 => new CaseRuleConjunct(CaseRuleConjunctKind.NotEqual, path, [domain[random.Next(domain.Length)]]),
                    3 => new CaseRuleConjunct(CaseRuleConjunctKind.In, governed, Pick(random, strings)),
                    4 => new CaseRuleConjunct(CaseRuleConjunctKind.NotEqual, governed, [strings[random.Next(strings.Length)]]),
                    _ => new CaseRuleConjunct(CaseRuleConjunctKind.Number, governed, []),
                });
            }

            return new CaseRuleProgram(seed, discriminators.ToArray(), branches.ToArray(), numbers.ToArray(), domains.ToArray(), conjuncts.ToArray());
        }

        /// <summary>
        /// Gets the paths a discriminator governs.
        /// </summary>
        /// <param name="discriminator">The discriminator's path, <c>k{i}</c>.</param>
        /// <returns>Its two governed paths.</returns>
        public static string[] Governed(string discriminator) => [$"g{discriminator[1..]}0", $"g{discriminator[1..]}1"];

        /// <summary>
        /// Picks a non-empty subset of <paramref name="pool"/>, in its order.
        /// </summary>
        static string[] Pick(Random random, string[] pool) =>
            pool.Where(_ => random.Next(2) == 0).DefaultIfEmpty(pool[random.Next(pool.Length)]).ToArray();

        /// <summary>
        /// Renders the program for a failure message.
        /// </summary>
        /// <returns>The rendering.</returns>
        public override string ToString()
        {
            var text = new StringBuilder($"seed {Seed}\n");
            foreach (var (path, values) in Discriminators)
                text.Append($"  discriminator {path} in {{{string.Join(", ", values)}}}\n");

            foreach (var branch in Branches)
                text.Append("  ").Append(branch).Append('\n');

            foreach (var path in Numbers)
                text.Append($"  types({path}) = {{number}}\n");

            foreach (var (path, values) in Domains)
                text.Append($"  dom({path}) = {{{string.Join(", ", values)}}}\n");

            return text.Append("  predicate ").Append(string.Join(" and ", Conjuncts.Select(c => c.ToString()))).ToString();
        }

    }

}

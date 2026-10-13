using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Eggnog.Tests.Differential.Splits
{

    /// <summary>
    /// A random discriminated schema and a query over it (§8.2, <c>SplitOracleTests</c>): one or two discriminators with two
    /// to four values each, branches that declare presence, stored forms and domains of the paths each discriminator
    /// governs, and a query of <c>IN</c> lists and disequalities on the discriminators and comparisons on governed paths.
    /// </summary>
    /// <remarks>
    /// Each discriminator governs its own paths, so a fact never needs the values of two discriminators at once: a split on
    /// each discriminator alone (the scoped encoding's) and the prototype's nested splits prove the same facts.
    /// </remarks>
    /// <param name="Seed">The seed that made it.</param>
    /// <param name="Discriminators">Each discriminator's path and domain.</param>
    /// <param name="Branches">The branches.</param>
    /// <param name="Conjuncts">The query's conjuncts.</param>
    public sealed record SplitOracleProgram(int Seed, (string Path, string[] Values)[] Discriminators, SplitOracleBranch[] Branches,
        SplitOracleConjunct[] Conjuncts)
    {

        /// <summary>
        /// The stored forms a branch may declare.
        /// </summary>
        public static readonly string[] Forms = ["uuid-lower", "instant-ms"];

        /// <summary>
        /// Generates the program of a seed.
        /// </summary>
        /// <param name="seed">The seed.</param>
        /// <returns>The program.</returns>
        public static SplitOracleProgram Generate(int seed)
        {
            var random = new Random(seed);
            string[] values = ["a", "b", "c", "d"];
            string[] governedValues = ["x", "y", "z"];
            var discriminators = new List<(string Path, string[] Values)>();
            var branches = new List<SplitOracleBranch>();
            for (var i = 0; i < random.Next(1, 3); i++)
            {
                var path = "k" + i;
                var domain = values.OrderBy(_ => random.Next()).Take(random.Next(2, 5)).Order(StringComparer.Ordinal).ToArray();
                discriminators.Add((path, domain));
                string[] governed = [$"g{i}0", $"g{i}1"];
                foreach (var value in domain)
                {
                    var present = governed.Where(_ => random.Next(3) > 0).ToArray();
                    var forms = governed.Where(_ => random.Next(2) == 0).Select(g => (g, Forms[random.Next(Forms.Length)])).ToArray();
                    var domains = governed.Where(_ => random.Next(2) == 0)
                        .Select(g => (g, governedValues.Where(_ => random.Next(2) == 0).DefaultIfEmpty(governedValues[random.Next(3)]).ToArray()))
                        .ToArray();
                    branches.Add(new SplitOracleBranch(path, value, present, forms, domains));
                }
            }

            var conjuncts = new List<SplitOracleConjunct>();
            for (var n = random.Next(1, 4); conjuncts.Count < n;)
            {
                var (path, domain) = discriminators[random.Next(discriminators.Count)];
                var governed = $"g{path[1..]}{random.Next(2)}";
                switch (random.Next(4))
                {
                    case 0:
                    case 1:
                    {
                        // Sometimes a value outside the domain, which narrows nothing but must not hurt.
                        var pool = random.Next(4) == 0 ? domain.Append("e").ToArray() : domain;
                        var list = pool.Where(_ => random.Next(2) == 0).DefaultIfEmpty(pool[random.Next(pool.Length)]).ToArray();
                        conjuncts.Add(new SplitOracleConjunct(SplitOracleConjunctKind.In, path, list));
                        break;
                    }
                    case 2:
                        conjuncts.Add(new SplitOracleConjunct(SplitOracleConjunctKind.NotEqual, path, [domain[random.Next(domain.Length)]]));
                        break;
                    default:
                        conjuncts.Add(new SplitOracleConjunct(SplitOracleConjunctKind.Uuid, governed, ["u" + random.Next(3)]));
                        break;
                }
            }

            return new SplitOracleProgram(seed, discriminators.ToArray(), branches.ToArray(), conjuncts.ToArray());
        }

        /// <summary>
        /// Gets every path the query mentions, in order.
        /// </summary>
        public IReadOnlyList<string> Mentioned => Conjuncts.Select(c => c.Path).Distinct().Order(StringComparer.Ordinal).ToArray();

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

            return text.Append("  WHERE ").Append(string.Join(" AND ", Conjuncts.Select(c => c.ToString()))).ToString();
        }

    }

}

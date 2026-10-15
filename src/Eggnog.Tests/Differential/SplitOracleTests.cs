using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

using Eggnog.Tests.Differential.Splits;

using Xunit;

namespace Eggnog.Tests.Differential
{

    /// <summary>
    /// The split oracle: random discriminated schemas (one or two discriminators with two to four values; branch rules
    /// declaring presence, stored forms and domains) and queries with in-set conjuncts and disequalities, run in the
    /// scoped encoding with engine splits (<see cref="SplitOracleScoped"/>) and in the literal encoding with splits
    /// written as rules (<see cref="SplitOracleLiteral"/>). In the query, for every path the query mentions, presence,
    /// stored forms and contradiction must agree, and so must which paths have a domain; each domain the engine lifts
    /// must lie within the literal encoding's, which also joins the domains of the cases that went vacuous, and
    /// equal it where no conjunct narrows the path's discriminator, so that no case can go vacuous (see
    /// <see cref="Agree"/>). 300 seeds by default; <c>EGGNOG_SEEDS</c> raises it. A failing program prints itself.
    /// </summary>
    public sealed class SplitOracleTests
    {

        /// <summary>
        /// The number of seeds without <c>EGGNOG_SEEDS</c>.
        /// </summary>
        const int DefaultSeeds = 300;

        /// <summary>
        /// Every seed's program agrees in both encodings: engine splits in the scoped one and splits written as rules
        /// in the literal one (see <see cref="Agree"/>). At most three failing programs are reported.
        /// </summary>
        [Fact]
        public void EngineSplitsAgreeWithSplitsWrittenAsRules()
        {
            var failures = new List<string>();
            for (var seed = 0; seed < Seeds() && failures.Count < 3; seed++)
                if (Run(seed) is { } failure)
                    failures.Add(failure);

            Assert.True(failures.Count == 0, string.Join("\n\n", failures));
        }

        /// <summary>
        /// The generator produces every shape the comparison relies on: two discriminators, four-valued ones,
        /// in-set conjuncts of more than one value, disequalities, and branches with stored forms and with domains; and
        /// the first 60 programs open cases in more than ten queries and refute at least one.
        /// </summary>
        [Fact]
        public void TheGeneratedProgramsCoverEveryShapeUnderTest()
        {
            var programs = Enumerable.Range(0, DefaultSeeds).Select(SplitOracleProgram.Generate).ToList();
            Assert.Contains(programs, p => p.Discriminators.Length == 2);
            Assert.Contains(programs, p => p.Discriminators.Any(d => d.Values.Length == 4));
            Assert.Contains(programs, p => p.Conjuncts.Any(c => c.Kind == SplitOracleConjunctKind.In && c.Values.Length > 1));
            Assert.Contains(programs, p => p.Conjuncts.Any(c => c.Kind == SplitOracleConjunctKind.NotEqual));
            Assert.Contains(programs, p => p.Branches.Any(b => b.Forms.Length > 0) && p.Branches.Any(b => b.Domains.Length > 0));

            // The runs exercise splits that open cases, and queries the cases refute.
            var opened = 0;
            var contradicted = 0;
            foreach (var program in programs.Take(60))
            {
                var facts = SplitOracleScoped.Run(program, out var report);
                opened += report.CasesOpened > 0 ? 1 : 0;
                contradicted += facts.Contradictory ? 1 : 0;
            }

            Assert.True(opened > 10, $"{opened} of 60 queries opened cases");
            Assert.True(contradicted > 0, "no query was refuted");
        }

        /// <summary>
        /// The comparison itself (<see cref="Agree"/>): a lifted domain narrower than the literal encoding's, on a path
        /// whose discriminator no conjunct narrows, is a disagreement. No case of either encoding can be vacuous there,
        /// so a value missing from the engine's lift is a lost fact (a case join that keeps one case's value, say), not
        /// the literal encoding's wider join; a check of containment alone would accept it.
        /// </summary>
        [Fact]
        public void ANarrowerDomainWhereNoCaseCanBeVacuousDisagrees()
        {
            for (var seed = 0; seed < DefaultSeeds; seed++)
            {
                var program = SplitOracleProgram.Generate(seed);
                var scoped = SplitOracleScoped.Run(program, out _);
                if (scoped.Contradictory)
                    continue;

                foreach (var (path, domain) in scoped.Domains)
                {
                    if (domain.Count < 2 || Narrowed(program, Governor(path)))
                        continue;

                    var literal = SplitOracleLiteral.Run(program);
                    Assert.Null(Agree(program, scoped, literal));

                    var lost = new SplitOracleFacts { Contradictory = scoped.Contradictory };
                    lost.Present.UnionWith(scoped.Present);
                    lost.Forms.UnionWith(scoped.Forms);
                    foreach (var (other, value) in scoped.Domains)
                        lost.Domains[other] = other == path ? value.Remove(value[^1]) : value;

                    Assert.True(lost.Domains[path].IsSubsetOf(literal.Domains[path]));        // containment alone accepts it
                    Assert.Contains("no conjunct narrows", Agree(program, lost, literal), StringComparison.Ordinal);
                    return;
                }
            }

            Assert.Fail("no seed lifts a domain of two values or more on a path whose discriminator no conjunct narrows");
        }

        /// <summary>
        /// Runs one seed; returns a description of the disagreement, or <c>null</c>.
        /// </summary>
        static string? Run(int seed)
        {
            var program = SplitOracleProgram.Generate(seed);
            SplitOracleFacts scoped;
            SplitOracleFacts literal;
            try
            {
                scoped = SplitOracleScoped.Run(program, out _);
                literal = SplitOracleLiteral.Run(program);
            }
            catch (Exception exception)
            {
                return $"{program}\nthrew {exception}";
            }

            if (Agree(program, scoped, literal) is { } difference)
                return $"{program}\n{difference}\n  scoped:  {scoped}\n  literal: {literal}";

            return null;
        }

        /// <summary>
        /// Compares the two encodings' facts; returns what differs, or <c>null</c>. A domain the engine lifts may be
        /// narrower than the literal encoding's, and only when a case went vacuous (seed 374, beyond the default 300
        /// seeds: a query <c>k0 in {a, c}</c> over the domain {a, b, c}; the literal encoding's split takes k0 = b
        /// before the conjunct narrows the domain, and joins that vacuous case's domains of the other paths into
        /// what it lifts). A case goes vacuous only where a conjunct narrows the discriminator that governs the path
        /// (<see cref="Narrowed"/>): elsewhere the domains must be equal.
        /// </summary>
        internal static string? Agree(SplitOracleProgram program, SplitOracleFacts scoped, SplitOracleFacts literal)
        {
            if (scoped.Contradictory != literal.Contradictory)
                return "contradiction differs";

            // A query with no model proves everything in the literal encoding, which keeps deriving; nothing to
            // compare.
            if (scoped.Contradictory)
                return null;

            if (scoped.Present.SetEquals(literal.Present) == false)
                return "presence differs";

            if (scoped.Forms.SetEquals(literal.Forms) == false)
                return "stored forms differ";

            if (scoped.Domains.Keys.SequenceEqual(literal.Domains.Keys, StringComparer.Ordinal) == false)
                return "the paths with a domain differ";

            // A domain the engine lifts is the case join of its live cases' domains. The literal encoding's rule "lift
            // a domain" joins the domains every case reported, a vacuous one's included (it keeps deriving after its
            // contradiction), and its interleaved schedule splits before a query's in-set conjunct or disequality narrowed the
            // domain, so it opens cases that go vacuous. Its domain is then wider, never narrower: the engine's must
            // lie within it, and equal it when no case went vacuous.
            foreach (var (path, domain) in scoped.Domains)
            {
                if (domain.IsSubsetOf(literal.Domains[path]) == false)
                    return $"the domain of {path} is not within the literal encoding's";

                if (Narrowed(program, Governor(path)) == false && domain.Equals(literal.Domains[path]) == false)
                    return $"the domain of {path} differs from the literal encoding's, and no conjunct narrows {Governor(path)}";
            }

            return null;
        }

        /// <summary>
        /// Gets the discriminator that governs a path the query mentions: <c>k{i}</c> itself, or <c>k{i}</c> for a
        /// governed path <c>g{i}{j}</c> (<see cref="SplitOracleProgram.Generate"/>).
        /// </summary>
        static string Governor(string path) => "k" + path[1];

        /// <summary>
        /// Gets whether a conjunct of the query rules out a value of a discriminator's domain: an in-set conjunct on it
        /// that leaves out a value of its domain, or a disequality on it with a value of its domain. Only then can a
        /// case of a split on it be vacuous.
        /// </summary>
        static bool Narrowed(SplitOracleProgram program, string discriminator)
        {
            var domain = program.Discriminators.First(d => d.Path == discriminator).Values;
            foreach (var conjunct in program.Conjuncts)
            {
                if (conjunct.Path != discriminator)
                    continue;

                if (conjunct.Kind == SplitOracleConjunctKind.In && domain.Any(v => conjunct.Values.Contains(v) == false))
                    return true;

                if (conjunct.Kind == SplitOracleConjunctKind.NotEqual && domain.Contains(conjunct.Values[0]))
                    return true;
            }

            return false;
        }

        /// <summary>
        /// The number of seeds: <c>EGGNOG_SEEDS</c>, or 300.
        /// </summary>
        static int Seeds() =>
            int.TryParse(Environment.GetEnvironmentVariable("EGGNOG_SEEDS"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var seeds) && seeds > 0
                ? seeds
                : DefaultSeeds;

    }

}

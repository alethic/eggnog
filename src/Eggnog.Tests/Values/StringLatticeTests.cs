using System;
using System.Globalization;
using System.Linq;
using System.Text;

using Xunit;

namespace Eggnog.Tests.Values
{

    /// <summary>
    /// The built-in <see cref="ValueLattice.Max{T}"/> and <see cref="ValueLattice.Min{T}"/> over strings: they order
    /// ordinally, as <see cref="Set{T}"/> does, so merges and case joins give the same result under every current
    /// culture and obey the lattice laws on strings that a culture collates as equal.
    /// </summary>
    public sealed class StringLatticeTests
    {

        /// <summary>
        /// Strings a culture-sensitive comparison orders differently from an ordinal one: <c>"z"</c> and <c>"ä"</c>
        /// (sv-SE sorts ä after z), <c>"I"</c> and dotless <c>"ı"</c> (tr-TR sorts I after ı), precomposed and
        /// decomposed <c>"é"</c> (canonically equivalent), <c>"a"</c> and <c>"a"</c> with a soft hyphen (ignorable),
        /// <c>"B"</c> (after <c>"a"</c> in every culture, before it ordinally), and the empty string.
        /// </summary>
        static readonly string[] Samples =
        {
            "z",
            "\u00E4",
            "I",
            "\u0131",
            "\u00E9",
            "e\u0301",
            "a",
            "a\u00AD",
            "B",
            "",
        };

        /// <summary>
        /// Fragments the random samples are built from: the cases above, and a supplementary character and U+FFFD,
        /// which UTF-16 code-unit order and code-point order disagree on.
        /// </summary>
        static readonly string[] Fragments =
        {
            "a", "B", "z", "\u00E4", "I", "\u0131", "\u00E9", "e", "\u0301", "\u00AD", "\uFFFD", "\U0001F600",
        };

        /// <summary>
        /// The cultures every test runs under.
        /// </summary>
        public static readonly TheoryData<string> Cultures = new() { "en-US", "sv-SE", "tr-TR" };

        /// <summary>
        /// Runs <paramref name="action"/> with <paramref name="culture"/> as the current culture, and restores the
        /// previous one.
        /// </summary>
        /// <param name="culture">The culture's name.</param>
        /// <param name="action">The test body.</param>
        static void InCulture(string culture, Action action)
        {
            var saved = CultureInfo.CurrentCulture;
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
            try
            {
                action();
            }
            finally
            {
                CultureInfo.CurrentCulture = saved;
            }
        }

        /// <summary>
        /// The premise of the other tests: under each culture, the current culture's comparison orders the samples
        /// differently from ordinal order, so a lattice that compared by culture would fail them.
        /// </summary>
        [Theory]
        [MemberData(nameof(Cultures))]
        public void TheCultureComparisonDiffersFromOrdinalOnTheSamples(string culture)
        {
            InCulture(culture, () =>
            {
                Assert.Equal(0, "\u00E9".CompareTo("e\u0301"));        // canonically equivalent
                Assert.NotEqual(0, string.CompareOrdinal("\u00E9", "e\u0301"));
                Assert.Equal(0, "a".CompareTo("a\u00AD"));             // the soft hyphen is ignorable
                Assert.NotEqual(0, string.CompareOrdinal("a", "a\u00AD"));
                Assert.True("a".CompareTo("B") < 0);
                Assert.True(string.CompareOrdinal("a", "B") > 0);
            });
        }

        /// <summary>
        /// <see cref="ValueLattice{T}.Verify"/> accepts <c>Max&lt;string&gt;</c> and <c>Min&lt;string&gt;</c> over
        /// strings the culture collates as equal but that differ ordinally.
        /// </summary>
        [Theory]
        [MemberData(nameof(Cultures))]
        public void VerifyAcceptsMaxAndMinOverCultureSensitiveStrings(string culture)
        {
            InCulture(culture, () =>
            {
                ValueLattice.Max<string>().Verify(Samples);
                ValueLattice.Min<string>().Verify(Samples);
            });
        }

        /// <summary>
        /// The merges and case joins of pairs that cultures order differently give the ordinal result under every
        /// culture.
        /// </summary>
        [Theory]
        [MemberData(nameof(Cultures))]
        public void MergesAndCaseJoinsAreTheSameUnderEveryCulture(string culture)
        {
            InCulture(culture, () =>
            {
                var max = ValueLattice.Max<string>();
                var min = ValueLattice.Min<string>();
                foreach (var (low, high) in new[] { ("z", "\u00E4"), ("I", "\u0131"), ("B", "a"), ("e\u0301", "\u00E9"), ("a", "a\u00AD") })
                {
                    Assert.Equal(high, max.Merge(low, high));
                    Assert.Equal(high, max.Merge(high, low));
                    Assert.Equal(low, max.CaseJoin!(low, high));
                    Assert.Equal(low, max.CaseJoin!(high, low));
                    Assert.Equal(low, min.Merge(low, high));
                    Assert.Equal(low, min.Merge(high, low));
                    Assert.Equal(high, min.CaseJoin!(low, high));
                    Assert.Equal(high, min.CaseJoin!(high, low));
                }
            });
        }

        /// <summary>
        /// The maximum of random strings is the last element of their <see cref="Set{T}"/>, and the minimum the
        /// first: the lattices and sets share one order.
        /// </summary>
        [Theory]
        [MemberData(nameof(Cultures))]
        public void MaxAndMinAgreeWithTheOrderOfValueSet(string culture)
        {
            InCulture(culture, () =>
            {
                var max = ValueLattice.Max<string>();
                var min = ValueLattice.Min<string>();
                var random = new Random(20261013);
                for (var round = 0; round < 200; round++)
                {
                    var samples = new string[random.Next(1, 8)];
                    for (var i = 0; i < samples.Length; i++)
                    {
                        var text = new StringBuilder();
                        var length = random.Next(0, 4);
                        for (var j = 0; j < length; j++)
                            text.Append(Fragments[random.Next(Fragments.Length)]);
                        samples[i] = text.ToString();
                    }

                    var set = Set.Of(samples);
                    Assert.Equal(set[set.Count - 1], samples.Aggregate(max.Merge));
                    Assert.Equal(set[0], samples.Aggregate(min.Merge));
                    Assert.Equal(set[0], samples.Aggregate(max.CaseJoin!));
                    Assert.Equal(set[set.Count - 1], samples.Aggregate(min.CaseJoin!));
                }
            });
        }

        /// <summary>
        /// A lift joins the cases' values of a <c>Min&lt;string&gt;</c> function by the ordinal maximum, under every
        /// culture.
        /// </summary>
        [Theory]
        [MemberData(nameof(Cultures))]
        public void ALiftJoinsAMinOfStringsOrdinally(string culture)
        {
            InCulture(culture, () =>
            {
                var graph = new EGraph();
                var bound = graph.DefineFunction<string, string>("bound", ValueLattice.Min<string>());
                var root = graph.Root;

                using var cases = root.OpenCases();
                cases.Add().Assume(bound.Of("letter").Is("z"), bound.Of("dotted").Is("I"), bound.Of("case").Is("B"));
                cases.Add().Assume(bound.Of("letter").Is("\u00E4"), bound.Of("dotted").Is("\u0131"), bound.Of("case").Is("a"));
                var lifted = cases.Lift();

                Assert.Equal(3, lifted.Values);
                Assert.Equal("\u00E4", root.Get(bound.Of("letter")));
                Assert.Equal("\u0131", root.Get(bound.Of("dotted")));
                Assert.Equal("a", root.Get(bound.Of("case")));

                root.Assume(bound.Of("case").Is("B"));   // and the merge is the ordinal minimum
                Assert.Equal("B", root.Get(bound.Of("case")));
            });
        }

    }

}

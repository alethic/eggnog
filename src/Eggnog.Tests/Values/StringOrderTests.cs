using System;
using System.Linq;

using Xunit;

namespace Eggnog.Tests.Values
{

    /// <summary>
    /// Strings compare by code point, as Rust's do, where UTF-16 code units would put U+E000-U+FFFF after the
    /// supplementary characters: in the comparer itself, in a set's element order, and in the <c>Max</c> lattice.
    /// </summary>
    public sealed class StringOrderTests
    {

        static readonly string[] Samples =
        {
            "", "a", "ab", "퟿", "", "�", "￿", "\U00010000", "\U0010FFFF", "a\U00010000", "a�",
            "\U00010000a", "�a",
        };

        /// <summary>
        /// The comparer agrees with comparing the strings' Unicode scalar values one by one, for every pair of samples.
        /// </summary>
        [Fact]
        public void TheComparerOrdersByCodePoint()
        {
            foreach (var x in Samples)
            {
                foreach (var y in Samples)
                {
                    var expected = Math.Sign(CompareScalars(x, y));
                    Assert.Equal(expected, Math.Sign(Relations.CodePointComparer.Instance.Compare(x, y)));
                }
            }

            Assert.True(Relations.CodePointComparer.Instance.Compare("�", "\U00010000") < 0);
            Assert.True(string.CompareOrdinal("�", "\U00010000") > 0);
        }

        [Fact]
        public void ASetOrdersItsStringsByCodePoint()
        {
            var set = Set.Of("\U00010000", "�", "a");

            Assert.Equal(new[] { "a", "�", "\U00010000" }, set.ToArray());
        }

        [Fact]
        public void TheMaxLatticeOrdersStringsByCodePoint()
        {
            var graph = new EGraph();
            var best = graph.DefineFunction<string, string>("best", ValueLattice.Max<string>());
            graph.Root.Assume(best.Of("k").Is("\U00010000"), best.Of("k").Is("�"));

            Assert.Equal("\U00010000", graph.Root.Get(best.Of("k")));
        }

        /// <summary>
        /// Compares two strings by their Unicode scalar values, one by one, then by length.
        /// </summary>
        static int CompareScalars(string x, string y)
        {
            var a = x.EnumerateRunes().Select(r => r.Value).ToArray();
            var b = y.EnumerateRunes().Select(r => r.Value).ToArray();
            for (var i = 0; i < Math.Min(a.Length, b.Length); i++)
                if (a[i] != b[i])
                    return a[i].CompareTo(b[i]);

            return a.Length.CompareTo(b.Length);
        }

    }

}

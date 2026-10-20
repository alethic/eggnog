using Xunit;

namespace Eggnog.Tests.Terms
{

    /// <summary>
    /// egglog's <c>bool</c> and <c>String</c> primitives in <see cref="BooleanPrimitives"/> and
    /// <see cref="StringPrimitives"/>: their egglog names, and their values, which for strings are Rust's <c>str</c>
    /// semantics, an empty pattern included.
    /// </summary>
    public sealed class BooleanAndStringPrimitiveTests
    {

        public static TheoryData<Primitive, string> Names => new()
        {
            { BooleanPrimitives.Not, "not" },
            { BooleanPrimitives.And, "and" },
            { BooleanPrimitives.Or, "or" },
            { BooleanPrimitives.Xor, "xor" },
            { BooleanPrimitives.Implies, "=>" },
            { StringPrimitives.Concat, "+" },
            { StringPrimitives.Replace, "replace" },
            { StringPrimitives.CountMatches, "count-matches" },
        };

        [Theory]
        [MemberData(nameof(Names))]
        public void EachPrimitiveHasEgglogsNameAndIsTotal(Primitive primitive, string name)
        {
            Assert.Equal(name, primitive.Name);
            Assert.False(primitive.IsPartial);
        }

        [Theory]
        [InlineData(false, false)]
        [InlineData(false, true)]
        [InlineData(true, false)]
        [InlineData(true, true)]
        public void TheBooleanPrimitivesAreTheirTruthTables(bool a, bool b)
        {
            Assert.Equal(!a, BooleanPrimitives.Not.Function(a));
            Assert.Equal(a && b, BooleanPrimitives.And.Function(a, b));
            Assert.Equal(a || b, BooleanPrimitives.Or.Function(a, b));
            Assert.Equal(a != b, BooleanPrimitives.Xor.Function(a, b));
            Assert.Equal(!a || b, BooleanPrimitives.Implies.Function(a, b));
        }

        [Fact]
        public void ConcatJoinsTwoStrings()
        {
            Assert.Equal("ab", StringPrimitives.Concat.Function("a", "b"));
            Assert.Equal("a", StringPrimitives.Concat.Function("a", ""));
        }

        [Theory]
        [InlineData("aaa", "a", "b", "bbb")]
        [InlineData("aaaa", "aa", "b", "bb")]
        [InlineData("aaa", "aa", "b", "ba")]
        [InlineData("abc", "d", "x", "abc")]
        [InlineData("abc", "b", "", "ac")]
        [InlineData("ab", "", "-", "-a-b-")]
        [InlineData("", "", "-", "-")]
        [InlineData("\U0001F600", "", "-", "-\U0001F600-")]
        [InlineData("aBA", "a", "x", "xBA")]
        public void ReplaceIsRustsStrReplace(string text, string pattern, string replacement, string result) =>
            Assert.Equal(result, StringPrimitives.Replace.Function(text, pattern, replacement));

        [Theory]
        [InlineData("aaaa", "aa", 2)]
        [InlineData("aaa", "aa", 1)]
        [InlineData("abcabc", "bc", 2)]
        [InlineData("abc", "d", 0)]
        [InlineData("abc", "", 4)]
        [InlineData("", "", 1)]
        [InlineData("\U0001F600", "", 2)]
        public void CountMatchesIsRustsStrMatchesCount(string text, string pattern, long count) =>
            Assert.Equal(count, StringPrimitives.CountMatches.Function(text, pattern));

    }

}

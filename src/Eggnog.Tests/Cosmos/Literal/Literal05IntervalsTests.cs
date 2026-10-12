using Xunit;

namespace Eggnog.Tests.Cosmos.Literal
{

    /// <summary>
    /// <c>prototypes/cosmos-facts/05-intervals.egg</c>, as written (gap 4): a bound is two lattice-valued functions with
    /// <c>max</c> and <c>min</c> as their merges and one contradiction rule, so <c>n &gt; 500</c> against
    /// <c>maximum: 100</c> is false, and <c>n &gt; 50</c> is left alone. Each <c>(push)</c> block is a fresh graph; the
    /// expected figures are the egglog CLI's (vendored <c>ext/egglog</c> at 5e655cae).
    /// </summary>
    public sealed class Literal05IntervalsTests
    {

        /// <summary>
        /// The prelude plus the file's top-level facts: <c>n</c> is a number in [0, 100].
        /// </summary>
        static PrototypeTheory Container()
        {
            var c = new PrototypeTheory(new EGraph());
            c.Ground();
            c.Graph.Root.Assume(
                c.Types.Of(c.Schema.Of(), "n").Is(ValueSet.Of("number")),
                c.Lo.Of(c.Schema.Of(), "n").Is(0L),
                c.Hi.Of(c.Schema.Of(), "n").Is(100L));
            return c;
        }

        [Fact]
        public void AComparisonBeyondADeclaredBoundIsFalse()
        {
            var c = Container();
            var root = c.Graph.Root;
            var q = root.Make(c.NumGt.Of("n", 500L));
            root.Assume(c.WhereClause.Of(q));

            var report = root.Run(Schedule.Saturate(c.Horn, c.Lattice, c.Split, c.Rewrite));

            Assert.True(report.Saturated);
            Assert.True(root.Check(c.Contradicts.Of(c.Query.Of())));
            Assert.True(root.AreEqual(q, c.PFalse.Of()));
            PrintSize.Matches(c, "(And 0) (Assume 0) (BoolTrue 0) (InstantGt 0) (IsDefined 0) (IsString 0) (Not 0) (NumGt 1) (Or 0) (PFalse 1) (PTrue 0) (Query 1) (Schema 1) (StrEq 0) (StrGe 0) (StrGt 0) (UuidEq 0) (admits 0) (conjunct 1) (contradiction 1) (discriminator 0) (dom 0) (dom-cases 0) (dom-reported 0) (form 0) (form-cases 0) (hi 2) (inset 0) (lo 2) (ne 0) (parent 1) (present 1) (present-cases 0) (types 2) (where 1)");
            var best = root.Extract(q);
            Assert.Equal("(PFalse)", best.ToString());
            Assert.Equal(0, best.Cost);
        }

        [Fact]
        public void ABoundInsideTheRangeIsLeftAlone()
        {
            var c = Container();
            var root = c.Graph.Root;
            var q = root.Make(c.NumGt.Of("n", 50L));
            root.Assume(c.WhereClause.Of(q));

            var report = root.Run(Schedule.Saturate(c.Horn, c.Lattice, c.Split, c.Rewrite));

            Assert.True(report.Saturated);
            Assert.False(root.Check(c.Contradicts.Of(c.Query.Of())));
            Assert.Equal(51L, root.Get(c.Lo.Of(c.Query.Of(), "n")));
            Assert.True(root.Check(c.Lo.Of(c.Query.Of(), "n").Is(51L)));
            Assert.Equal(100L, root.Get(c.Hi.Of(c.Query.Of(), "n")));
            PrintSize.Matches(c, "(And 0) (Assume 0) (BoolTrue 0) (InstantGt 0) (IsDefined 0) (IsString 0) (Not 0) (NumGt 1) (Or 0) (PFalse 0) (PTrue 0) (Query 1) (Schema 1) (StrEq 0) (StrGe 0) (StrGt 0) (UuidEq 0) (admits 0) (conjunct 1) (contradiction 0) (discriminator 0) (dom 0) (dom-cases 0) (dom-reported 0) (form 0) (form-cases 0) (hi 2) (inset 0) (lo 2) (ne 0) (parent 1) (present 1) (present-cases 0) (types 2) (where 1)");
            var best = root.Extract(q);
            Assert.Equal("(NumGt \"n\" 50)", best.ToString());
            Assert.Equal(3, best.Cost);
        }

    }

}

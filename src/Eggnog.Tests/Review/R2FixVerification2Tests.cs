using Eggnog.Tests.Support;

using Xunit;

namespace Eggnog.Tests.Review
{

    /// <summary>
    /// Probes of the R2 fixes (verification round 2): the N86 run-time comparison of class constants under semi-naive
    /// evaluation (X19, I30: a constant that loses a union forces a full run), and D16/D49 (a query or a read never moves
    /// Version) in a descendant that must catch up with an ancestor's write. Q01-Q04 and Q07 pass at eb6929c; Q05 and Q06
    /// fail there: the descendant's catch-up is lazy, so the first query or read after the ancestor's write moves its
    /// Version.
    /// </summary>
    public sealed class R2FixVerification2Tests
    {

        /// <summary>
        /// An Equal step of a global rule: no match at the root, then the root unions the two classes. The atom's row is
        /// old, so only the constants' change can make the rule run in full again (I30).
        /// </summary>
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Q01_AnEqualStepMatchesAfterTheRootUnionsItsConstants(bool reversed)
        {
            var t = new NodeTheory();
            var a = t.Root.Make(t.A.Of());
            var lit = t.Root.Make(t.Lit.Of(1L));
            t.Root.Assume(t.Tag.Of(a));
            t.Rules.DefineRule("through a variable", r =>
            {
                var x = r.Var<Node>("x");
                r.When(t.Tag.Of(x), x.Is(a), x.Is(lit));
                r.Then(t.Num.Of(3L));
            });
            t.Rules.DefineRule("direct", r =>
            {
                r.When(((Term<Node>)a).Is(lit));
                r.Then(t.Num.Of(2L));
            });

            t.Root.Run(Schedule.Saturate(t.Rules));
            Assert.False(t.Root.Check(t.Num.Of(3L)));
            Assert.False(t.Root.Check(t.Num.Of(2L)));

            if (reversed)
                t.Root.Union(lit, a);
            else
                t.Root.Union(a, lit);

            t.Root.Run(Schedule.Saturate(t.Rules));
            Assert.True(t.Root.Check(t.Num.Of(3L)));
            Assert.True(t.Root.Check(t.Num.Of(2L)));
        }

        /// <summary>
        /// The same in a child that ran before its union: its watermarks must not hide the new match.
        /// </summary>
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Q02_AnEqualStepMatchesAfterAChildThatAlreadyRanUnionsItsConstants(bool reversed)
        {
            var t = new NodeTheory();
            var a = t.Root.Make(t.A.Of());
            var lit = t.Root.Make(t.Lit.Of(1L));
            t.Root.Assume(t.Tag.Of(a));
            t.Rules.DefineRule("through a variable", r =>
            {
                var x = r.Var<Node>("x");
                r.When(t.Tag.Of(x), x.Is(a), x.Is(lit));
                r.Then(t.Num.Of(3L));
            });
            t.Rules.DefineRule("direct", r =>
            {
                r.When(((Term<Node>)a).Is(lit));
                r.Then(t.Num.Of(2L));
            });
            t.Root.Freeze();
            var child = t.Root.CreateChild();
            child.Run(Schedule.Saturate(t.Rules));
            Assert.False(child.Check(t.Num.Of(3L)));

            if (reversed)
                child.Union(lit, a);
            else
                child.Union(a, lit);

            child.Run(Schedule.Saturate(t.Rules));
            Assert.True(child.Check(t.Num.Of(3L)));
            Assert.True(child.Check(t.Num.Of(2L)));
        }

        /// <summary>
        /// A local rule's constants, different in the owner, unioned later in the owner itself after a run.
        /// </summary>
        [Fact]
        public void Q03_ALocalRulesEqualStepMatchesAfterTheOwnerUnionsItsConstants()
        {
            var t = new NodeTheory();
            var a = t.Root.Make(t.A.Of());
            var b = t.Root.Make(t.B.Of());
            t.Root.Freeze();
            var owner = t.Root.CreateChild();
            owner.Assume(t.Tag.Of(a));
            owner.DefineRule(t.Rules, "equated", r =>
            {
                var x = r.Var<Node>("x");
                r.When(t.Tag.Of(x), x.Is(a), x.Is(b));
                r.Then(t.Num.Of(7L));
            });
            owner.Run(Schedule.Saturate(t.Rules));
            Assert.False(owner.Check(t.Num.Of(7L)));

            owner.Union(b, a);
            owner.Run(Schedule.Saturate(t.Rules));
            Assert.True(owner.Check(t.Num.Of(7L)));
        }

        /// <summary>
        /// A query's Equal step on a writable root, before and after the root unions the constants.
        /// </summary>
        [Fact]
        public void Q04_AQuerysEqualStepFollowsTheRootsUnions()
        {
            var t = new NodeTheory();
            var a = t.Root.Make(t.A.Of());
            var b = t.Root.Make(t.B.Of());
            t.Root.Assume(t.Tag.Of(a));
            var query = t.Graph.DefineQuery("equated", q =>
            {
                var x = q.Var<Node>("x");
                q.When(t.Tag.Of(x), x.Is(a), x.Is(b));
            });
            Assert.Equal(0, t.Root.Count(query));
            t.Root.Union(b, a);
            Assert.Equal(1, t.Root.Count(query));
        }

        /// <summary>
        /// N62 / D16 / D49: a query never moves Version. Control: a plain query in a descendant whose ancestor wrote a row the
        /// descendant's union touches (the catch-up that the query's iteration makes).
        /// </summary>
        [Fact]
        public void Q05_APlainQueryInADescendantAfterAnAncestorWriteKeepsItsVersion()
        {
            var t = new NodeTheory();
            var a = t.Root.Make(t.A.Of());
            var b = t.Root.Make(t.B.Of());
            var query = t.Graph.DefineQuery("tags", q => q.When(t.Tag.Of(q.Var<Node>("x"))));
            var outer = t.Root.CreateChild();
            var inner = outer.CreateChild();
            inner.Union(a, b);
            Assert.Equal(0, inner.Count(query));
            var version = inner.Version;

            outer.Assume(t.Tag.Of(a), t.Tag.Of(b));
            Assert.Equal(1, inner.Count(query));
            Assert.Equal(version, inner.Version);
        }

        /// <summary>
        /// The same with a host read (D16: reads never move Version): <c>Check</c> after the ancestor's write.
        /// </summary>
        [Fact]
        public void Q06_AHostReadInADescendantAfterAnAncestorWriteKeepsItsVersion()
        {
            var t = new NodeTheory();
            var a = t.Root.Make(t.A.Of());
            var b = t.Root.Make(t.B.Of());
            var outer = t.Root.CreateChild();
            var inner = outer.CreateChild();
            inner.Union(a, b);
            Assert.False(inner.Check(t.Tag.Of(a)));
            var version = inner.Version;

            outer.Assume(t.Tag.Of(a), t.Tag.Of(b));
            Assert.Equal(version, inner.Version);
            Assert.True(inner.Check(t.Tag.Of(b)));
            Assert.Equal(version, inner.Version);
        }

        /// <summary>
        /// A child inherits the root's watermarks (colors §10.2): the root ran the rule without a match, froze, and the child
        /// unions the constants. The inherited watermark was stored with the root's constants, so the child runs in full.
        /// </summary>
        [Fact]
        public void Q07_AnInheritedWatermarkDoesNotHideAChildsUnionOfTheConstants()
        {
            var t = new NodeTheory();
            var a = t.Root.Make(t.A.Of());
            var lit = t.Root.Make(t.Lit.Of(1L));
            t.Root.Assume(t.Tag.Of(a));
            t.Rules.DefineRule("through a variable", r =>
            {
                var x = r.Var<Node>("x");
                r.When(t.Tag.Of(x), x.Is(a), x.Is(lit));
                r.Then(t.Num.Of(3L));
            });
            t.Rules.DefineRule("direct", r =>
            {
                r.When(((Term<Node>)a).Is(lit));
                r.Then(t.Num.Of(2L));
            });
            t.Root.Run(Schedule.Saturate(t.Rules));
            Assert.False(t.Root.Check(t.Num.Of(3L)));
            t.Root.Freeze();

            var child = t.Root.CreateChild();
            child.Union(lit, a);
            child.Run(Schedule.Saturate(t.Rules));
            Assert.True(child.Check(t.Num.Of(3L)));
            Assert.True(child.Check(t.Num.Of(2L)));
        }

    }

}

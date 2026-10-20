using System;
using System.Linq;

using Eggnog.Tests.Support;

using Xunit;

namespace Eggnog.Tests.Subsumption
{

    /// <summary>
    /// Host subsumption through <see cref="Scope.Subsume{TSort}(ConstructorTerm{TSort})"/>, <see
    /// cref="Scope.Subsume(Fact[])"/> and a <see cref="ScopeWriter"/>: an existing row is subsumed, a missing one is
    /// made subsumed (egglog's <c>test_subsume_ok</c>), a row already subsumed writes nothing, and a batch subsumes the
    /// terms and rows it makes, whichever call comes first.
    /// </summary>
    public sealed class SubsumeHostTests
    {

        [Fact]
        public void ExistingAndMissingRowsAreSubsumed()
        {
            // test_subsume_ok: subsuming present and missing constructor terms and relation rows.
            var t = new MathTheory();
            var one = t.Graph.DefineConstructor<Node>("one", 1, MathTheory.Subsumable);
            var two = t.Graph.DefineConstructor<Node>("two", 1, MathTheory.Subsumable);
            var r = t.Graph.DefineRelation<long>("R", MathTheory.Subsumable);
            t.Root.Make(one.Of());
            t.Root.Assume(r.Of(1L));

            t.Root.Subsume(one.Of());
            t.Root.Subsume(two.Of());
            t.Root.Subsume(r.Of(1L), r.Of(2L));

            Assert.True(Assert.Single(t.Root.Rows(one)).Subsumed);
            Assert.True(Assert.Single(t.Root.Rows(two)).Subsumed);
            Assert.True(t.Root.TryGet(two.Of(), out _));
            Assert.Equal(new[] { (1L, true), (2L, true) }, t.Root.Rows(r).Select(row => ((long)row.Keys[0], row.Subsumed)));
        }

        [Fact]
        public void TheSubtermsOfAMissingTermAreMadeNotSubsumed()
        {
            var t = new MathTheory();

            t.Root.Subsume(t.Add.Of(t.Num.Of(1L), t.Num.Of(2L)));

            Assert.True(Assert.Single(t.Root.Rows(t.Add)).Subsumed);
            Assert.All(t.Root.Rows(t.Num), row => Assert.False(row.Subsumed));
            Assert.Equal(2L, t.Root.CountRows(t.Num));
        }

        [Fact]
        public void SubsumingWhatIsSubsumedWritesNothing()
        {
            var t = new MathTheory();
            var fact = t.Graph.DefineRelation<string>("fact", MathTheory.Subsumable);
            t.Root.Make(t.Num.Of(1L));
            t.Root.Assume(fact.Of("a"));
            var before = t.Root.Version;

            t.Root.Subsume(t.Num.Of(1L));
            t.Root.Subsume(fact.Of("a"));
            var subsumed = t.Root.Version;
            t.Root.Subsume(t.Num.Of(1L));
            t.Root.Subsume(fact.Of("a"));
            t.Root.Make(t.Num.Of(1L));
            t.Root.Assume(fact.Of("a"));

            Assert.True(subsumed > before);
            Assert.Equal(subsumed, t.Root.Version);
            Assert.True(Assert.Single(t.Root.Rows(t.Num)).Subsumed);
            Assert.True(Assert.Single(t.Root.Rows(fact)).Subsumed);
        }

        [Fact]
        public void ABatchSubsumesWhatItMakesWhicheverCallComesFirst()
        {
            var t = new MathTheory();
            var fact = t.Graph.DefineRelation<string>("fact", MathTheory.Subsumable);

            t.Root.Write(w =>
            {
                w.Make(t.Num.Of(1L));
                w.Subsume(t.Num.Of(1L));
                w.Subsume(t.Num.Of(2L));
                w.Make(t.Num.Of(2L));
                w.Assume(fact.Of("a"));
                w.Subsume(fact.Of("a"));
                w.Subsume(fact.Of("b"));
                w.Assume(fact.Of("b"));
                w.Make(t.Num.Of(3L));
            });

            var nums = t.Root.Rows(t.Num).Select(row => ((long)row.Keys[0], row.Subsumed));
            Assert.Equal(new[] { (1L, true), (2L, true), (3L, false) }, nums);
            Assert.Equal(new[] { ("a", true), ("b", true) }, t.Root.Rows(fact).Select(row => ((string)row.Keys[0], row.Subsumed)));
        }

        [Fact]
        public void ARejectedSubsumptionRejectsItsBatch()
        {
            var t = new MathTheory();
            var plain = t.Graph.DefineConstructor<long, Node>("Plain");
            var version = t.Root.Version;

            t.Root.Write(w =>
            {
                w.Make(t.Num.Of(1L));
                w.Subsume(t.Num.Of(2L));
                Assert.Throws<ArgumentException>(() => w.Subsume(plain.Of(1L)));
            });

            Assert.Equal(version, t.Root.Version);
            Assert.Equal(0L, t.Root.CountRows(t.Num));
        }

        [Fact]
        public void ASubsumptionSurvivesAUnionAndTheClassKeepsItsOtherMembers()
        {
            var t = new MathTheory();
            var one = t.Root.Make(t.Num.Of(1L));
            var x = t.Root.Make(t.Var.Of("x"));
            t.Root.Subsume(t.Num.Of(1L));

            Assert.True(t.Root.Union(one, x));

            Assert.True(t.Root.AreEqual(x, t.Num.Of(1L)));
            Assert.True(Assert.Single(t.Root.Rows(t.Num)).Subsumed);
            Assert.False(Assert.Single(t.Root.Rows(t.Var)).Subsumed);
            Assert.Equal("(Var \"x\")", t.Root.Extract(one).ToString());
        }

    }

}

using System;

using Eggnog.Tests.Support;

using Xunit;

namespace Eggnog.Tests.Host
{

    /// <summary>
    /// Host reads (§6.6.3): every row of the check table, values and classes, and reads that never intern or fail for want
    /// of a rebuild.
    /// </summary>
    public sealed class HostReadTests
    {

        [Fact]
        public void ARelationRowHoldsWhenEveryArgumentReadsAndTheRowExists()
        {
            var t = new NodeTheory();
            var pair = t.Graph.DefineRelation<Node, long>("pair");
            t.Root.Assume(pair.Of(t.Lit.Of(1L), 2L));
            Assert.True(t.Root.Check(pair.Of(t.Lit.Of(1L), 2L)));
            Assert.False(t.Root.Check(pair.Of(t.Lit.Of(1L), 3L)));
            Assert.False(t.Root.Check(pair.Of(t.Lit.Of(9L), 2L)));
            Assert.True(t.Root.Check(pair.Of(t.Root.Get(t.Lit.Of(1L)), 2L)));
            Assert.True(t.Root.Check());
        }

        [Fact]
        public void AnEqualityOfClassesHoldsWhenTheyAreOneClass()
        {
            var t = new NodeTheory();
            var a = t.Root.Make(t.A.Of());
            t.Root.Make(t.B.Of());
            Assert.False(t.Root.Check(t.A.Of().Is(t.B.Of())));
            Assert.True(t.Root.Check(t.A.Of().Is(a)));
            t.Root.Assume(t.A.Of().Is(t.B.Of()));
            Assert.True(t.Root.Check(t.A.Of().Is(t.B.Of())));
            Assert.False(t.Root.Check(t.A.Of().Is(t.Lit.Of(1L))));
        }

        [Fact]
        public void AnEqualityOfValuesHoldsWhenTheCellsAreEqual()
        {
            var t = new NodeTheory();
            var f = t.Graph.DefineFunction<string, long>("f");
            t.Root.Assume(f.Of("k").Is(1L), f.Of("j").Is(1L), f.Of("i").Is(2L));
            Assert.True(t.Root.Check(f.Of("k").Is(1L)));
            Assert.False(t.Root.Check(f.Of("k").Is(2L)));
            Assert.True(t.Root.Check(f.Of("k").Is(f.Of("j"))));
            Assert.False(t.Root.Check(f.Of("k").Is(f.Of("i"))));
            Assert.False(t.Root.Check(f.Of("absent").Is(1L)));
            Assert.True(t.Root.Check(((Term<long>)3L).Is(3L)));
        }

        [Fact]
        public void AnExistenceHoldsWhenTheApplicationHasARow()
        {
            var t = new NodeTheory();
            var f = t.Graph.DefineFunction<string, long>("f");
            t.Root.Assume(f.Of("k").Is(1L));
            t.Root.Make(t.Wrap.Of(t.A.Of()));
            Assert.True(t.Root.Check(Fact.Exists(f.Of("k"))));
            Assert.False(t.Root.Check(Fact.Exists(f.Of("j"))));
            Assert.True(t.Root.Check(Fact.Exists(t.Wrap.Of(t.A.Of()))));
            Assert.False(t.Root.Check(Fact.Exists(t.Wrap.Of(t.B.Of()))));
            Assert.Throws<ArgumentException>(() => Fact.Exists(Primitives.Add.Of(1L, 2L)));
        }

        [Fact]
        public void FiltersAreNotChecks()
        {
            var t = new NodeTheory();
            Assert.Throws<ArgumentException>(() => t.Root.Check(((Term<long>)1L).IsNot(2L)));
            Assert.Throws<ArgumentException>(() => t.Root.Check(Fact.True(true)));
            Assert.False(t.Root.IsFaulted);
        }

        [Fact]
        public void GetAndTryGetReadValuesAndClasses()
        {
            var t = new NodeTheory();
            var f = t.Graph.DefineFunction<string, long>("f");
            var size = t.Graph.DefineFunction<Node, ValueSet<string>>("size");
            t.Root.Assume(f.Of("k").Is(1L), size.Of(t.Lit.Of(1L)).Is(ValueSet.Of("x")));

            Assert.Equal(1L, t.Root.Get(f.Of("k")));
            Assert.True(t.Root.TryGet(f.Of("k"), out var value));
            Assert.Equal(1L, value);
            Assert.False(t.Root.TryGet(f.Of("j"), out _));
            Assert.Throws<InvalidOperationException>(() => t.Root.Get(f.Of("j")));
            Assert.Equal(ValueSet.Of("x"), t.Root.Get(size.Of(t.Lit.Of(1L))));

            var lit = t.Root.Get(t.Lit.Of(1L));
            Assert.Equal(t.Root.Find(lit).Id, lit.Id);
            Assert.True(t.Root.AreEqual(lit, t.Lit.Of(1L)));
            Assert.False(t.Root.TryGet(t.Lit.Of(2L), out _));
        }

        [Fact]
        public void AreEqualWithAnAbsentTermIsFalse()
        {
            var t = new NodeTheory();
            var a = t.Root.Make(t.A.Of());
            Assert.False(t.Root.AreEqual(a, t.B.Of()));
            Assert.False(t.Root.AreEqual(a, t.Wrap.Of(t.B.Of())));
            Assert.True(t.Root.AreEqual(a, t.A.Of()));
            Assert.False(t.Root.Check(Fact.Exists(t.B.Of())));
        }

        [Fact]
        public void ReadsNeverIntern()
        {
            var t = new NodeTheory();
            var text = t.Graph.DefineRelation<string>("text");
            var f = t.Graph.DefineFunction<string, ValueSet<string>>("f");
            var byName = t.Graph.DefineConstructor<string, Node>("ByName");
            var a = t.Root.Make(t.A.Of());
            t.Root.Assume(text.Of("seen"), f.Of("seen").Is(ValueSet.Of("s")));
            var before = t.Graph.Values.InternedCount;

            Assert.False(t.Root.Check(text.Of("unseen 1")));
            Assert.False(t.Root.TryGet(f.Of("unseen 2"), out _));
            Assert.False(t.Root.AreEqual(a, byName.Of("unseen 3")));
            Assert.False(t.Root.Check(f.Of("seen").Is(ValueSet.Of("unseen 4"))));
            Assert.False(t.Root.Check(Fact.Exists(byName.Of("unseen 5"))));
            Assert.False(t.Root.Check(t.Num.Of(long.MinValue + 7)));

            Assert.Equal(before, t.Graph.Values.InternedCount);
        }

        [Fact]
        public void ReadsNeverFailForWantOfARebuild()
        {
            var t = new NodeTheory();
            var a = t.Root.Make(t.Wrap.Of(t.A.Of()));
            var b = t.Root.Make(t.Wrap.Of(t.B.Of()));
            Assert.False(t.Root.AreEqual(a, b));

            t.Root.Union(t.Root.Make(t.A.Of()), t.Root.Make(t.B.Of()));

            // Congruence: the rebuild after the union merged the wrappers, and reads see it at once.
            Assert.True(t.Root.AreEqual(a, b));
            Assert.True(t.Root.Check(t.Wrap.Of(t.A.Of()).Is(t.Wrap.Of(t.B.Of()))));
            Assert.Equal(t.Root.Find(a).Id, t.Root.Get(t.Wrap.Of(t.B.Of())).Id);
        }

        [Fact]
        public void IsContradictoryReadsTheContradictionRelation()
        {
            var t = new NodeTheory();
            Assert.False(t.Root.IsContradictory);
            Assert.False(t.Root.Check(t.Graph.Contradiction.Of()));
            t.Root.Assume(t.Graph.Contradiction.Of());
            Assert.True(t.Root.IsContradictory);
            Assert.True(t.Root.Check(t.Graph.Contradiction.Of()));
        }

        [Fact]
        public void ReadsAreGround()
        {
            var t = new NodeTheory();
            var f = t.Graph.DefineFunction<long, long>("f");
            Assert.Throws<ArgumentException>(() => t.Root.Get(f.Of(Primitives.Add.Of(1L, 2L))));
            Assert.Throws<ArgumentException>(() => t.Root.Get(Primitives.Add.Of(1L, 2L)));
            var other = new NodeTheory();
            Assert.Throws<ArgumentException>(() => t.Root.Check(other.Num.Of(1L)));
        }

    }

}

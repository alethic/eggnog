using System;
using System.Linq;

using Eggnog.Tests.Support;

using Xunit;

namespace Eggnog.Tests.Host
{

    /// <summary>
    /// Host reads: every row of the check table, values and classes, rows in content order, and reads that never intern
    /// or fail for want of a rebuild.
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

        [Fact]
        public void CountRowsCountsATablesRows()
        {
            var t = new NodeTheory();
            var f = t.Graph.DefineFunction<string, long>("f");
            Assert.Equal(0, t.Root.CountRows(t.Num));
            t.Root.Assume(t.Num.Of(3L), t.Num.Of(1L), t.Num.Of(3L), f.Of("a").Is(1L), f.Of("b").Is(2L));
            Assert.Equal(2, t.Root.CountRows(t.Num));
            Assert.Equal(2, t.Root.CountRows(f));
            Assert.Equal(0, t.Root.CountRows(t.Lit));
            Assert.Equal(0, t.Root.CountRows(t.Graph.Contradiction));

            t.Root.Make(t.Add.Of(t.Lit.Of(1L), t.Lit.Of(2L)));
            Assert.Equal(2, t.Root.CountRows(t.Lit));
            Assert.Equal(1, t.Root.CountRows(t.Add));

            // Rows of classes that merge become one row.
            t.Root.Assume(t.Lit.Of(1L).Is(t.Lit.Of(2L)));
            t.Root.Make(t.Wrap.Of(t.Lit.Of(1L)));
            t.Root.Make(t.Wrap.Of(t.Lit.Of(2L)));
            Assert.Equal(1, t.Root.CountRows(t.Wrap));
        }

        [Fact]
        public void RowsDecodeEveryRowInContentOrder()
        {
            var t = new NodeTheory();
            var named = t.Graph.DefineRelation<string, long>("named");
            var dom = t.Graph.DefineFunction<string, ValueSet<string>>("dom", ValueLattice.Intersect<string>());
            t.Root.Assume(named.Of("b", 10L), named.Of("a", 2L), named.Of("b", 9L), named.Of("Z", 1L));
            t.Root.Assume(dom.Of("x").Is(ValueSet.Of("q", "p")));

            var rows = t.Root.Rows(named);
            Assert.Equal(new[] { "(named \"Z\" 1)", "(named \"a\" 2)", "(named \"b\" 9)", "(named \"b\" 10)" }, rows.Select(r => r.ToString()));
            Assert.All(rows, r => Assert.Same(named, r.Table));
            Assert.All(rows, r => Assert.Null(r.Output));
            Assert.Equal(new object[] { "b", 10L }, rows[3].Keys);

            var values = t.Root.Rows(dom);
            Assert.Single(values);
            Assert.Equal(new object[] { "x" }, values[0].Keys);
            Assert.Equal(ValueSet.Of("p", "q"), values[0].Output);
            Assert.Empty(t.Root.Rows(t.Num));
        }

        [Fact]
        public void RowsGiveCanonicalClassesUsableInTheScope()
        {
            var t = new NodeTheory();
            var one = t.Root.Make(t.Lit.Of(1L));
            var two = t.Root.Make(t.Lit.Of(2L));
            var sum = t.Root.Make(t.Add.Of(one, two));
            t.Root.Union(one, two);

            var lits = t.Root.Rows(t.Lit);
            Assert.Equal(2, lits.Count);
            Assert.Equal(lits[0].Output, lits[1].Output);
            var literal = Node.FromId((ClassId)lits[0].Output!);
            Assert.Equal(t.Root.Find(one), literal);
            Assert.True(t.Root.AreEqual(literal, two));

            var adds = t.Root.Rows(t.Add);
            var add = Assert.Single(adds);
            Assert.Equal(literal.Id, add.Keys[0]);
            Assert.Equal(literal.Id, add.Keys[1]);
            Assert.Equal(t.Root.Find(sum).Id, add.Output);
            Assert.Equal($"(Add {literal.Id} {literal.Id}) = {t.Root.Find(sum).Id}", add.ToString());
        }

        [Fact]
        public void RowReadsNeverInternAndRefuseAnotherGraphsTable()
        {
            var t = new NodeTheory();
            var other = new NodeTheory();
            t.Root.Assume(t.Num.Of(1L));
            var interned = t.Graph.Values.InternedCount;
            var version = t.Root.Version;

            t.Root.Rows(t.Num);
            t.Root.CountRows(t.Num);

            Assert.Equal(interned, t.Graph.Values.InternedCount);
            Assert.Equal(version, t.Root.Version);
            Assert.Throws<ArgumentException>(() => t.Root.Rows(other.Num));
            Assert.Throws<ArgumentException>(() => t.Root.CountRows(other.Num));
            Assert.Throws<ArgumentNullException>(() => t.Root.Rows(null!));
            Assert.Throws<ArgumentNullException>(() => t.Root.CountRows(null!));
        }

        /// <summary>
        /// <c>CountRows</c> is egglog's <c>print-size</c>, and <c>Rows</c> lists the same rows, after congruence
        /// collapsed two.
        /// </summary>
        [Fact]
        public void RowsAndCountRowsAgreeAfterCongruence()
        {
            var t = new NodeTheory();
            var a = t.Root.Make(t.A.Of());
            var b = t.Root.Make(t.B.Of());
            t.Root.Make(t.Wrap.Of(a));
            t.Root.Make(t.Wrap.Of(b));
            Assert.Equal(2, t.Root.CountRows(t.Wrap));

            t.Root.Union(a, b);

            Assert.Equal(1, t.Root.CountRows(t.Wrap));
            Assert.Single(t.Root.Rows(t.Wrap));
        }

        [Fact]
        public void ContentOrderOfStrings()
        {
            var g = new EGraph();
            var s = g.DefineRelation<string>("s");
            var values = new[] { "b", "a", "B", "ä", "aa", "" };
            g.Root.Assume(values.Select(v => s.Of(v)).ToArray());
            var rows = g.Root.Rows(s).Select(r => (string)r.Keys[0]).ToArray();
            Assert.Equal(values.OrderBy(v => v, StringComparer.Ordinal).ToArray(), rows);
        }

        [Fact]
        public void ContentOrderOfDoubles()
        {
            var g = new EGraph();
            var s = g.DefineRelation<double>("d");
            var values = new[] { 1.5, -2.5, 0.0, 1e300, -1e-300, 3.0 };
            g.Root.Assume(values.Select(v => s.Of(v)).ToArray());
            var rows = g.Root.Rows(s).Select(r => (double)r.Keys[0]).ToArray();
            Assert.Equal(values.OrderBy(v => v).ToArray(), rows);
        }

        [Fact]
        public void CountRowsAndRowsAgree()
        {
            var t = new NodeTheory();
            var a = t.Root.Make(t.Lit.Of(1L));
            var b = t.Root.Make(t.Lit.Of(2L));
            t.Root.Make(t.Wrap.Of(a));
            t.Root.Make(t.Wrap.Of(b));
            t.Root.Union(a, b);
            Assert.Equal(t.Root.Rows(t.Wrap).Count, t.Root.CountRows(t.Wrap));
            Assert.Equal(1, t.Root.CountRows(t.Wrap));
            Assert.Equal(2, t.Root.CountRows(t.Lit));
            Assert.Equal(0, t.Root.CountRows(t.Graph.Contradiction));
            t.Root.Assume(t.Graph.Contradiction.Of());
            Assert.Equal(1, t.Root.CountRows(t.Graph.Contradiction));
            Assert.Equal("($contradiction)", t.Root.Rows(t.Graph.Contradiction)[0].ToString());
        }

    }

}

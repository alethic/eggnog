using System;
using System.Linq;
using System.Text;

using Xunit;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// Tests for <see cref="TermDag"/>: egglog's termdag.rs tests (src/termdag.rs:526-606) that do not need the parser,
    /// with the terms built directly, plus hash-consing and deep-term checks.
    /// </summary>
    public class TermDagTests
    {

        /// <summary>
        /// Builds <c>(f (g x y) x y (g x y))</c> left to right, depth first, as egglog's <c>expr_to_term</c> does.
        /// </summary>
        static TermId Sample(TermDag dag)
        {
            var x = dag.Var("x");
            var y = dag.Var("y");
            var g = dag.App("g", x, y);
            var x2 = dag.Var("x");
            var y2 = dag.Var("y");
            var g2 = dag.App("g", dag.Var("x"), dag.Var("y"));
            return dag.App("f", g, x2, y2, g2);
        }

        [Fact]
        public void ToFromExpr()
        {
            // The expression has 4 distinct subterms; in left-to-right, depth-first order: x, y, (g x y), f.
            var dag = new TermDag();
            Assert.Equal(0, dag.Count);
            var t = Sample(dag);
            Assert.Equal(4, dag.Count);

            Assert.Equal(TermKind.Var, dag[new TermId(0)].Kind);
            Assert.Equal("x", dag[new TermId(0)].Name);
            Assert.Equal("y", dag[new TermId(1)].Name);
            var g = dag[new TermId(2)];
            Assert.Equal(TermKind.App, g.Kind);
            Assert.Equal("g", g.Name);
            Assert.Equal([new TermId(0), new TermId(1)], g.Children.ToArray());
            var f = dag[t];
            Assert.Equal("f", f.Name);
            Assert.Equal([new TermId(2), new TermId(0), new TermId(1), new TermId(2)], f.Children.ToArray());
        }

        [Fact]
        public void MatchTermApp()
        {
            var dag = new TermDag();
            var f = dag[Sample(dag)];
            Assert.Equal("f", f.Name);
            Assert.Equal(4, f.Arity);
            Assert.Equal(TermKind.Var, f.Child(1).Kind);
            Assert.Equal("x", f.Child(1).Name);
            Assert.Throws<ArgumentOutOfRangeException>(() => f.Child(4));
        }

        [Fact]
        public void ToStringRendersTheTerm()
        {
            var dag = new TermDag();
            Assert.Equal("(f (g x y) x y (g x y))", dag.ToString(Sample(dag)));
        }

        [Fact]
        public void LookupFindsTheLastTerm()
        {
            var dag = new TermDag();
            var t = Sample(dag);
            Assert.Equal(dag.Count - 1, t.Rep);
            Assert.True(dag.TryLookupApp("f", dag[t].Children, out var found));
            Assert.Equal(t, found);
            Assert.True(dag.TryLookupVar("y", out var y));
            Assert.Equal(new TermId(1), y);
            Assert.False(dag.TryLookupVar("z", out _));
            Assert.False(dag.TryLookupApp("g", [y], out _));
            Assert.Equal(4, dag.Count);
        }

        [Fact]
        public void AppVarLitShareWithEqualTerms()
        {
            // (f (g x y) x 7 (g x y)), built twice, is one term.
            var dag = new TermDag();
            var registry = new BaseValues();
            var type = registry.RegisterType<Int64Codec, long>();
            TermId Build()
            {
                var x = dag.Var("x");
                var y = dag.Var("y");
                var seven = dag.Lit(new TermLiteral(type, registry.Get<Int64Codec, long>(7), "7"));
                var g = dag.App("g", x, y);
                return dag.App("f", g, x, seven, g);
            }

            var first = Build();
            var count = dag.Count;
            Assert.Equal(first, Build());
            Assert.Equal(count, dag.Count);
            Assert.Equal("(f (g x y) x 7 (g x y))", dag.ToString(first));
            Assert.Equal(TermKind.Lit, dag[first].Child(2).Kind);
            Assert.Equal("7", dag[first].Child(2).Literal.Text);
            Assert.True(dag.TryLookupLit(new TermLiteral(type, registry.Get<Int64Codec, long>(7), "7"), out _));
            Assert.False(dag.TryLookupLit(new TermLiteral(type, registry.Get<Int64Codec, long>(8), "8"), out _));
        }

        [Fact]
        public void TermsDifferingInAnyPartAreDistinct()
        {
            var dag = new TermDag();
            var x = dag.Var("x");
            var lit = dag.Lit(new TermLiteral(new BaseValueTypeId(0), new Value(1), "x"));
            Assert.NotEqual(x, lit);
            Assert.NotEqual(dag.App("x"), x);
            Assert.NotEqual(dag.App("f", x), dag.App("f", lit));
            Assert.NotEqual(dag.App("f", x, x), dag.App("f", x));
            Assert.NotEqual(dag.Lit(new TermLiteral(new BaseValueTypeId(1), new Value(1), "x")), lit);
            Assert.Equal(dag.App("f"), dag.App("f", ReadOnlySpan<TermId>.Empty));
            Assert.Equal("(x)", dag.ToString(dag.App("x")));
        }

        [Fact]
        public void BadInputsThrow()
        {
            var dag = new TermDag();
            Assert.Throws<ArgumentOutOfRangeException>(() => dag.App("f", new TermId(0)));
            Assert.Throws<ArgumentOutOfRangeException>(() => dag[new TermId(0)]);
            Assert.Throws<ArgumentOutOfRangeException>(() => dag.ToString(new TermId(-1)));
            Assert.Throws<ArgumentException>(() => dag.Lit(default));
            Assert.Throws<InvalidOperationException>(() => dag[dag.Var("v")].Literal);
            Assert.Throws<InvalidOperationException>(() => dag[dag.Lit(new TermLiteral(default, default, "1"))].Name);
        }

        [Fact]
        public void SharedSubtermsRenderOnceAndRepeat()
        {
            // A DAG of depth 20 doubling at every level renders to 2^20 leaves from 21 terms.
            var dag = new TermDag();
            var t = dag.Var("a");
            for (var i = 0; i < 20; i++)
                t = dag.App("p", t, t);

            Assert.Equal(21, dag.Count);
            var text = dag.ToString(t);
            Assert.Equal(1 << 20, text.Split('a').Length - 1);
            Assert.StartsWith("(p (p (p", text);
        }

        [Fact]
        public void DeepTermsDoNotOverflowTheStack()
        {
            var dag = new TermDag();
            var t = dag.App("z");
            const int Depth = 200_000;
            for (var i = 0; i < Depth; i++)
                t = dag.App("s", t);

            var text = dag.ToString(t);
            var expected = new StringBuilder();
            for (var i = 0; i < Depth; i++)
                expected.Append("(s ");

            expected.Append("(z)").Append(')', Depth);
            Assert.Equal(expected.ToString(), text);
        }

        [Fact]
        public void ManyTermsGrowTheArena()
        {
            var dag = new TermDag();
            var ids = Enumerable.Range(0, 5000).Select(i => dag.Var("v" + i)).ToArray();
            var apps = Enumerable.Range(0, 5000).Select(i => dag.App("f", ids[i], ids[(i * 7) % 5000])).ToArray();
            Assert.Equal(10_000, dag.Count);
            for (var i = 0; i < 5000; i++)
            {
                Assert.Equal(apps[i], dag.App("f", ids[i], ids[(i * 7) % 5000]));
                Assert.Equal($"(f v{i} v{(i * 7) % 5000})", dag.ToString(apps[i]));
            }

            Assert.Equal(10_000, dag.Count);
        }

    }

}

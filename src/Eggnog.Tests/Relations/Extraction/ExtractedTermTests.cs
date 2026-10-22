using System;

using Xunit;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// Tests for <see cref="ExtractedTerm"/>, the object form of an extracted term.
    /// </summary>
    public class ExtractedTermTests
    {

        [Fact]
        public void TermsCarrySortsValuesAndCostsAndShareSubterms()
        {
            var fx = new ExtractionFixture();
            var e = new EqSort("E");
            var vec = new TestVecSort("VecE", e);
            var num = fx.Constructor("Num", e, 2, fx.I64);
            var add = fx.Constructor("Add", e, 1, e, e);
            var list = fx.Constructor("List", e, 1, vec);
            var three = fx.Make(num, fx.Int(3));
            var sum = fx.Make(add, three, three);
            var items = fx.EGraph.GetContainerValue(new VecContainer(sum.Rep, three.Rep));
            var all = fx.Make(list, items);

            var extractor = Extractor.CreateOnDemand(fx.Schema);
            var term = extractor.ExtractTerm(e, all);
            Assert.Equal(ExtractedTermKind.Node, term.Kind);
            Assert.Equal(list, term.Function);
            Assert.Equal("List", term.Name);
            Assert.Same(e, term.Sort);
            Assert.Equal(all, term.Value);
            Assert.Equal<ulong>(1 + (1 + 3 + 3) + 3, term.Cost);

            var container = term.Child(0);
            Assert.Equal(ExtractedTermKind.Container, container.Kind);
            Assert.Same(vec, container.Sort);
            Assert.Equal("VecE", container.Name);
            Assert.Equal<ulong>(10, container.Cost);
            Assert.Throws<InvalidOperationException>(() => container.Function);

            var addTerm = container.Child(0);
            Assert.Equal("Add", addTerm.Name);
            Assert.Equal<ulong>(7, addTerm.Cost);
            Assert.Same(addTerm.Child(0), addTerm.Child(1));
            Assert.Same(addTerm.Child(0), container.Child(1));

            var literal = addTerm.Child(0).Child(0);
            Assert.Equal(ExtractedTermKind.Base, literal.Kind);
            Assert.Equal("3", literal.Name);
            Assert.Equal(fx.Int(3), literal.Value);
            Assert.Equal<ulong>(1, literal.Cost);
            Assert.Equal(0, literal.Arity);
            Assert.Throws<ArgumentOutOfRangeException>(() => literal.Child(0));

            Assert.True(term.Mentions(num));
            Assert.True(term.Mentions(add));
            Assert.False(literal.Mentions(num));
            Assert.Equal("(List (vec-of (Add (Num 3) (Num 3)) (Num 3)))", term.ToString());

            // The object form and the DAG form agree.
            var dag = new TermDag();
            Assert.True(extractor.TryExtractBest(e, all, dag, out var result));
            Assert.Equal(result.Term, term.AddTo(dag));
            Assert.Equal(result.Cost, term.Cost);
        }

        [Fact]
        public void BaseAndContainerRootsExtractAsTerms()
        {
            var fx = new ExtractionFixture();
            var vec = new TestVecSort("VecI", fx.I64);
            var holder = fx.Function("holder", vec);
            fx.Set(holder, [], fx.EGraph.GetContainerValue(new VecContainer(false)));
            var extractor = Extractor.ComputeCostsFromRootSorts(fx.Schema, [vec, fx.String]);

            var empty = extractor.ExtractTerm(vec, fx.Get(holder));
            Assert.Equal(ExtractedTermKind.Container, empty.Kind);
            Assert.Equal<ulong>(0, empty.Cost);
            Assert.Equal("(vec-empty)", empty.ToString());

            var text = extractor.ExtractTerm(fx.String, fx.Str("a\"b"));
            Assert.Equal(ExtractedTermKind.Base, text.Kind);
            // egglog's Literal::String display: raw between quotes, no escaping.
            Assert.Equal("\"a\"b\"", text.ToString());
        }

    }

}

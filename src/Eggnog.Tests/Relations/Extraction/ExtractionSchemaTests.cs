using System;
using System.Linq;

using Xunit;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// Tests for <see cref="ExtractionSchema"/> declarations and the function annotations extraction honours
    /// (<c>unextractable</c>, <c>internal_hidden</c>, <c>term_constructor</c> views; src/extract.rs:142-244, 759-818).
    /// </summary>
    public class ExtractionSchemaTests
    {

        /// <summary>
        /// A constructor's signature.
        /// </summary>
        static FunctionSignature Ctor(Sort output, params Sort[] inputs)
        {
            return new FunctionSignature { Inputs = inputs, Output = output, IsConstructor = true };
        }

        /// <summary>
        /// Adds a table to the e-graph only, with a failing default and an assert-equal merge.
        /// </summary>
        static FunctionId Table(ExtractionFixture fx, string name, params ColumnTy[] schema)
        {
            return fx.EGraph.AddTable(new FunctionConfig { Schema = schema, Default = DefaultVal.Fail, Merge = MergeFn.AssertEq, Name = name });
        }

        [Fact]
        public void DeclarationsAreValidated()
        {
            var fx = new ExtractionFixture();
            var e = new EqSort("E");
            var i64 = fx.ColumnOf(fx.I64);
            var leaf = Table(fx, "leaf", i64, ColumnTy.Id);

            // Wrong number of inputs, sorts that do not fit their columns, a non-e-class constructor output, a negative
            // cost, an undeclared function.
            Assert.Throws<ArgumentException>(() => fx.Schema.Declare(leaf, Ctor(e)));
            Assert.Throws<ArgumentException>(() => fx.Schema.Declare(leaf, Ctor(e, e)));
            Assert.Throws<ArgumentException>(() => fx.Schema.Declare(leaf, Ctor(e, fx.Bool)));
            Assert.Throws<ArgumentException>(() => fx.Schema.Declare(leaf, Ctor(fx.I64, fx.I64)));
            Assert.Throws<ArgumentException>(() => fx.Schema.Declare(leaf, new FunctionSignature { Inputs = [fx.I64], Output = new TestVecSort("V", e), IsConstructor = true }));
            Assert.Throws<ArgumentException>(() => fx.Schema.Declare(new FunctionId(99), Ctor(e)));
            Assert.False(fx.Schema.IsDeclared(leaf));
            Assert.Empty(fx.Schema.Sorts);

            fx.Schema.Declare(leaf, Ctor(e, fx.I64));
            Assert.True(fx.Schema.IsDeclared(leaf));
            Assert.Equal(new Sort[] { fx.I64, e }, fx.Schema.Sorts.ToArray());
            Assert.Throws<ArgumentException>(() => fx.Schema.Declare(leaf, Ctor(e, fx.I64)));

            // A sort's name must be its own; a failed declaration changes nothing.
            var other = Table(fx, "other", i64, ColumnTy.Id);
            Assert.Throws<ArgumentException>(() => fx.Schema.Declare(other, Ctor(e, new BaseSort<Int64Codec, long>("i64"))));
            Assert.Throws<ArgumentException>(() => fx.Schema.Declare(other, Ctor(new EqSort("i64"), fx.I64)));
            Assert.Throws<ArgumentException>(() => fx.Schema.Declare(other, Ctor(new EqSort("E"), fx.I64)));
            Assert.False(fx.Schema.IsDeclared(other));
            Assert.Equal(2, fx.Schema.Sorts.Count);
        }

        [Fact]
        public void ViewsNeedTheirClassAsTheLastKey()
        {
            var fx = new ExtractionFixture();
            var e = new EqSort("E");
            var view = new EqSort("View");
            var term = fx.Constructor("AddT", e, null, e, e);

            var bad = Table(fx, "bad", ColumnTy.Id, fx.ColumnOf(fx.I64), ColumnTy.Id);
            Assert.Throws<ArgumentException>(() => fx.Schema.Declare(bad, new FunctionSignature { Inputs = [e, fx.I64], Output = view, TermConstructor = term }));

            var nullary = Table(fx, "nullary", ColumnTy.Id);
            Assert.Throws<ArgumentException>(() => fx.Schema.Declare(nullary, new FunctionSignature { Inputs = [], Output = view, TermConstructor = term }));

            var unknown = Table(fx, "unknown", ColumnTy.Id, ColumnTy.Id);
            var signature = new FunctionSignature { Inputs = [e], Output = view, TermConstructor = new FunctionId(99) };
            Assert.Throws<ArgumentException>(() => fx.Schema.Declare(unknown, signature));
        }

        [Fact]
        public void UnextractableAndHiddenFunctionsAreSkippedUnlessAllowed()
        {
            var fx = new ExtractionFixture();
            var e = new EqSort("E");
            var cheap = fx.ConstructorWith("cheap", e, new FunctionSignature { Inputs = [], Output = e, IsConstructor = true, Cost = 1, Unextractable = true });
            var hidden = fx.ConstructorWith("hidden", e, new FunctionSignature { Inputs = [], Output = e, IsConstructor = true, Cost = 2, Hidden = true });
            var visible = fx.Constructor("visible", e, 10);
            var c = fx.Make(cheap);
            fx.Union(c, fx.Make(hidden));
            fx.Union(c, fx.Make(visible));

            Assert.Equal(("(visible)", 10UL), fx.Extract(e, c));

            var dag = new TermDag();
            var all = Extractor.ComputeCostsFromRootSorts(fx.Schema, [e], options: ExtractionOptions.AllowUnextractable);
            Assert.True(all.TryExtractBest(e, c, dag, out var result));
            Assert.Equal(("(cheap)", 1UL), (dag.ToString(result.Term), result.Cost));

            var hiddenAllowed = new ExtractionOptions { RespectUnextractable = true, RespectHidden = false };
            var onlyHidden = Extractor.ComputeCostsFromRootSorts(fx.Schema, [e], options: hiddenAllowed);
            Assert.True(onlyHidden.TryExtractBest(e, c, dag, out result));
            Assert.Equal(("(hidden)", 2UL), (dag.ToString(result.Term), result.Cost));

            // With no root sorts, the roots are the extractable output sorts: an unextractable-only sort is not one.
            var only = new EqSort("Only");
            fx.ConstructorWith("onlyUnextractable", only, new FunctionSignature { Inputs = [], Output = only, IsConstructor = true, Unextractable = true });
            Assert.Equal(new Sort[] { e }, Extractor.ComputeCostsFromRootSorts(fx.Schema).RootSorts);
            var allSorts = Extractor.ComputeCostsFromRootSorts(fx.Schema, options: ExtractionOptions.AllowUnextractable).RootSorts;
            Assert.Equal(new Sort[] { e, only }, allSorts);
        }

        [Fact]
        public void ViewsExtractUnderTheirTermConstructor()
        {
            // Proof encoding: AddT is a hidden term constructor; AddView(a, b, class) -> View is its view, whose rows
            // say that class = (AddT a b). Views extract under the term constructor's name and cost.
            var fx = new ExtractionFixture();
            var e = new EqSort("E");
            var view = new EqSort("View");
            var num = fx.Constructor("Num", e, 1, fx.I64);
            var addT = fx.ConstructorWith("AddT", e, new FunctionSignature { Inputs = [e, e], Output = e, IsConstructor = true, Cost = 5, Hidden = true });
            var addView = fx.EGraph.AddTable(new FunctionConfig
            {
                Schema = [ColumnTy.Id, ColumnTy.Id, ColumnTy.Id, ColumnTy.Id],
                Default = DefaultVal.FreshId,
                Merge = MergeFn.UnionId,
                Name = "AddView",
            });
            fx.Schema.Declare(addView, new FunctionSignature { Inputs = [e, e, e], Output = view, TermConstructor = addT });

            var one = fx.Make(num, fx.Int(1));
            var two = fx.Make(num, fx.Int(2));
            var sum = fx.EGraph.FreshId();
            fx.Make(addView, one, two, sum);

            var dag = new TermDag();
            var extractor = Extractor.ComputeCostsFromRootSorts(fx.Schema, [e]);
            Assert.True(extractor.TryExtractBest(e, sum, dag, out var result));
            Assert.Equal(("(AddT (Num 1) (Num 2))", 9UL), (dag.ToString(result.Term), result.Cost));

            // The cost model can price the view itself, or its term constructor.
            Assert.True(Extractor.ComputeCostsFromRootSorts(fx.Schema, [e], CostModel.Default.WithFunctionCost(addView, 3)).TryGetCost(e, sum, out var cost));
            Assert.Equal<ulong>(7, cost);
            Assert.True(Extractor.ComputeCostsFromRootSorts(fx.Schema, [e], CostModel.Default.WithFunctionCost(addT, 2)).TryGetCost(e, sum, out cost));
            Assert.Equal<ulong>(6, cost);

            // Proof extraction skips views and uses the hidden term table, which has no row for this class.
            var proofs = Extractor.ComputeCostsFromRootSorts(fx.Schema, [e], options: ExtractionOptions.AllowUnextractable);
            Assert.False(proofs.TryGetCost(e, sum, out _));
            Assert.True(Extractor.CreateOnDemand(fx.Schema).TryGetCost(e, sum, out cost));
            Assert.Equal<ulong>(9, cost);
        }

    }

}

using System;
using System.Linq;

using Xunit;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// Tests for <see cref="CostModel"/>: egglog's <c>TreeAdditiveCostModel</c> defaults, the <c>enode_cost</c>,
    /// <c>base_value_cost</c> and <c>container_cost</c> hooks as overrides, addition saturating at
    /// <see cref="ulong.MaxValue"/> as egglog's <c>u64</c> costs do, and cost functions that throw.
    /// </summary>
    public class CostModelTests
    {

        [Fact]
        public void DefaultsAreTreeAdditive()
        {
            // A head costs its declared cost, 1 by default; a base value 1; a container the sum of its elements.
            var fx = new ExtractionFixture();
            var e = new EqSort("E");
            var vec = new TestVecSort("VecE", e);
            var leaf = fx.Constructor("leaf", e, null);
            var heavy = fx.Constructor("heavy", e, 7, fx.I64);
            var list = fx.Constructor("list", e, 2, vec);
            var l = fx.Make(leaf);
            var h = fx.Make(heavy, fx.Int(3));
            var items = fx.EGraph.GetContainerValue(new VecContainer(l.Rep, h.Rep, l.Rep));
            var all = fx.Make(list, items);

            Assert.Equal(("(list (vec-of (leaf) (heavy 3) (leaf)))", 2 + 1 + 8 + 1UL), fx.Extract(e, all));
        }

        [Fact]
        public void FunctionCostsOverrideDeclaredCosts()
        {
            var fx = new ExtractionFixture();
            var e = new EqSort("E");
            var a = fx.Constructor("A", e, 1);
            var b = fx.Constructor("B", e, 5);
            fx.Union(fx.Make(a), fx.Make(b));

            Assert.Equal(("(A)", 1UL), fx.Extract(e, fx.Get(a)));
            Assert.Equal(("(B)", 0UL), fx.Extract(e, fx.Get(a), CostModel.Default.WithFunctionCost(b, 0)));
            Assert.Equal(("(B)", 5UL), fx.Extract(e, fx.Get(a), CostModel.Default.WithFunctionCost(a, 10)));
        }

        [Fact]
        public void NodeCostsAreComputedPerRow()
        {
            // Num n costs n: the cheapest number in the class wins, whatever the declared cost.
            var fx = new ExtractionFixture();
            var e = new EqSort("E");
            var num = fx.Constructor("Num", e, 100, fx.I64);
            var ids = new[] { 9L, 4L, 6L }.Select(n => fx.Make(num, fx.Int(n))).ToArray();
            fx.Union(ids[0], ids[1]);
            fx.Union(ids[1], ids[2]);

            var model = CostModel.Default.WithNodeCost(num, (function, row) =>
            {
                Assert.Equal(num, function);
                Assert.False(row.Subsumed);
                return (ulong)fx.EGraph.BaseValues.Unwrap<Int64Codec, long>(row.Values[0]);
            });
            Assert.Equal(("(Num 4)", 5UL), fx.Extract(e, ids[0], model));

            // A constant given later replaces the per-row cost, and the rows tie: egglog's (datatype E (Num i64 :cost 2))
            // with the same terms and unions extracts (Num 9), the first row stored.
            Assert.Equal(("(Num 9)", 3UL), fx.Extract(e, ids[0], model.WithFunctionCost(num, 2)));
        }

        [Fact]
        public void BaseValueCostsCanBeOverridden()
        {
            var fx = new ExtractionFixture();
            var e = new EqSort("E");
            var num = fx.Constructor("Num", e, 1, fx.I64);
            var flag = fx.Constructor("Flag", e, 1, fx.Bool);
            var n = fx.Make(num, fx.Int(12));
            var f = fx.Make(flag, fx.Boolean(true));
            fx.Union(n, f);

            Assert.Equal(("(Num 12)", 2UL), fx.Extract(e, n));
            Assert.Equal(("(Num 12)", 2UL), fx.Extract(e, n, CostModel.Default.WithBaseValueCost(fx.I64, 1)));
            Assert.Equal(("(Flag true)", 2UL), fx.Extract(e, n, CostModel.Default.WithBaseValueCost(fx.I64, 2)));
            Assert.Equal(("(Flag true)", 4UL), fx.Extract(e, n, CostModel.Default.WithBaseValueCost(fx.I64, 5).WithBaseValueCost(fx.Bool, 3)));
            var byValue = CostModel.Default
                .WithBaseValueCost(fx.I64, (sort, value) => (ulong)fx.EGraph.BaseValues.Unwrap<Int64Codec, long>(value))
                .WithBaseValueCost(fx.Bool, 20);
            Assert.Equal(("(Num 12)", 13UL), fx.Extract(e, n, byValue));
            Assert.Equal(("(Num 12)", 1UL), fx.Extract(e, n, CostModel.Default.WithDefaultBaseValueCost(0)));
            Assert.Equal<ulong>(0, CostModel.Default.WithDefaultBaseValueCost(0).DefaultBaseValueCost);
            Assert.Equal<ulong>(1, CostModel.Default.DefaultBaseValueCost);
        }

        [Fact]
        public void ContainerCostsAddToTheirElements()
        {
            var fx = new ExtractionFixture();
            var e = new EqSort("E");
            var vec = new TestVecSort("VecI", fx.I64);
            var holder = fx.Function("holder", vec);
            var items = fx.EGraph.GetContainerValue(new VecContainer(false, fx.Int(1).Rep, fx.Int(2).Rep));
            fx.Set(holder, [], items);
            var value = fx.Get(holder);

            Assert.Equal(("(vec-of 1 2)", 2UL), fx.Extract(vec, value));
            Assert.Equal(("(vec-of 1 2)", 12UL), fx.Extract(vec, value, CostModel.Default.WithContainerCost(vec, 10)));
            Assert.Equal(("(vec-of 1 2)", 2UL + 2), fx.Extract(vec, value, CostModel.Default.WithContainerCost(vec, (sort, v) =>
            {
                Assert.Same(vec, sort);
                return fx.EGraph.ContainerValues.TryGetValue<VecContainer>(v.Rep, out var c) ? (ulong)c.Length : throw new InvalidOperationException("not a vector");
            })));
        }

        [Fact]
        public void CostsSaturateAtTheMaximum()
        {
            var fx = new ExtractionFixture();
            var e = new EqSort("E");
            var huge = fx.Constructor("huge", e, ulong.MaxValue / 2 + 1);
            var twice = fx.Constructor("twice", e, 0, e, e);
            var h = fx.Make(huge);
            var t = fx.Make(twice, h, h);
            var tt = fx.Make(twice, t, t);

            Assert.Equal(ulong.MaxValue / 2 + 1, fx.Extract(e, h)?.Cost);
            Assert.Equal(ulong.MaxValue, fx.Extract(e, t)?.Cost);
            Assert.Equal(ulong.MaxValue, fx.Extract(e, tt)?.Cost);
            Assert.Equal(ulong.MaxValue, fx.Extract(e, tt, CostModel.Default.WithBaseValueCost(fx.I64, ulong.MaxValue))?.Cost);
        }

        [Fact]
        public void AFailingCostFunctionFailsTheExtraction()
        {
            var fx = new ExtractionFixture();
            var e = new EqSort("E");
            var num = fx.Constructor("Num", e, 1, fx.I64);

            Assert.Throws<ArgumentException>(() => CostModel.Default.WithBaseValueCost(e, 1));
            Assert.Throws<ArgumentException>(() => CostModel.Default.WithContainerCost(fx.I64, 1));

            // A cost function that throws fails the extraction that calls it.
            fx.Make(num, fx.Int(5));
            var failingRow = CostModel.Default.WithNodeCost(num, (_, _) => throw new InvalidOperationException("no cost"));
            Assert.Throws<InvalidOperationException>(() => Extractor.ComputeCostsFromRootSorts(fx.Schema, [e], failingRow));
            var failingValue = CostModel.Default.WithBaseValueCost(fx.I64, (_, _) => throw new InvalidOperationException("no cost"));
            Assert.Throws<InvalidOperationException>(() => Extractor.ComputeCostsFromRootSorts(fx.Schema, [e], failingValue));

            // On demand, the failure surfaces at the query, and leaves the extractor unusable.
            var onDemand = Extractor.CreateOnDemand(fx.Schema, failingRow);
            Assert.Throws<InvalidOperationException>(() => onDemand.TryGetCost(e, fx.Get(num, fx.Int(5)), out _));
            var error = Assert.Throws<InvalidOperationException>(() => onDemand.TryGetCost(fx.I64, fx.Int(1), out _));
            Assert.Contains("failed midway", error.Message);
        }

        [Fact]
        public void TheDefaultModelIsSharedAndImmutable()
        {
            var fx = new ExtractionFixture();
            var e = new EqSort("E");
            var a = fx.Constructor("A", e, 3);
            var changed = CostModel.Default.WithFunctionCost(a, 9);
            Assert.NotSame(CostModel.Default, changed);
            fx.Make(a);
            Assert.Equal<ulong?>(3, fx.Extract(e, fx.Get(a))?.Cost);
            Assert.Equal<ulong?>(9, fx.Extract(e, fx.Get(a), changed)?.Cost);
        }

    }

}

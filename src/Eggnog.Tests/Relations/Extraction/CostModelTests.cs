using System;
using System.Linq;

using Xunit;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// Tests for <see cref="CostModel"/>: egglog's <c>TreeAdditiveCostModel</c> defaults, the <c>enode_cost</c>,
    /// <c>base_value_cost</c> and <c>container_cost</c> hooks as overrides, saturating long addition and the rejection
    /// of negative costs.
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

            Assert.Equal(("(list (vec-of (leaf) (heavy 3) (leaf)))", 2 + 1 + 8 + 1L), fx.Extract(e, all));
        }

        [Fact]
        public void FunctionCostsOverrideDeclaredCosts()
        {
            var fx = new ExtractionFixture();
            var e = new EqSort("E");
            var a = fx.Constructor("A", e, 1);
            var b = fx.Constructor("B", e, 5);
            fx.Union(fx.Make(a), fx.Make(b));

            Assert.Equal(("(A)", 1L), fx.Extract(e, fx.Get(a)));
            Assert.Equal(("(B)", 0L), fx.Extract(e, fx.Get(a), CostModel.Default.WithFunctionCost(b, 0)));
            Assert.Equal(("(B)", 5L), fx.Extract(e, fx.Get(a), CostModel.Default.WithFunctionCost(a, 10)));
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
                return fx.EGraph.BaseValues.Unwrap<Int64Codec, long>(row.Values[0]);
            });
            Assert.Equal(("(Num 4)", 5L), fx.Extract(e, ids[0], model));

            // A constant given later replaces the per-row cost.
            Assert.Equal(("(Num 4)", 3L), fx.Extract(e, ids[0], model.WithFunctionCost(num, 2)));
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

            Assert.Equal(("(Num 12)", 2L), fx.Extract(e, n));
            Assert.Equal(("(Num 12)", 2L), fx.Extract(e, n, CostModel.Default.WithBaseValueCost(fx.I64, 1)));
            Assert.Equal(("(Flag true)", 2L), fx.Extract(e, n, CostModel.Default.WithBaseValueCost(fx.I64, 2)));
            Assert.Equal(("(Flag true)", 4L), fx.Extract(e, n, CostModel.Default.WithBaseValueCost(fx.I64, 5).WithBaseValueCost(fx.Bool, 3)));
            var byValue = CostModel.Default
                .WithBaseValueCost(fx.I64, (sort, value) => fx.EGraph.BaseValues.Unwrap<Int64Codec, long>(value))
                .WithBaseValueCost(fx.Bool, 20);
            Assert.Equal(("(Num 12)", 13L), fx.Extract(e, n, byValue));
            Assert.Equal(("(Num 12)", 1L), fx.Extract(e, n, CostModel.Default.WithDefaultBaseValueCost(0)));
            Assert.Equal(0, CostModel.Default.WithDefaultBaseValueCost(0).DefaultBaseValueCost);
            Assert.Equal(1, CostModel.Default.DefaultBaseValueCost);
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

            Assert.Equal(("(vec-of 1 2)", 2L), fx.Extract(vec, value));
            Assert.Equal(("(vec-of 1 2)", 12L), fx.Extract(vec, value, CostModel.Default.WithContainerCost(vec, 10)));
            Assert.Equal(("(vec-of 1 2)", 2L + 2), fx.Extract(vec, value, CostModel.Default.WithContainerCost(vec, (sort, v) =>
            {
                Assert.Same(vec, sort);
                return fx.EGraph.ContainerValues.TryGetValue<VecContainer>(v.Rep, out var c) ? c.Length : -1;
            })));
        }

        [Fact]
        public void CostsSaturateAtTheMaximum()
        {
            var fx = new ExtractionFixture();
            var e = new EqSort("E");
            var huge = fx.Constructor("huge", e, long.MaxValue / 2 + 1);
            var twice = fx.Constructor("twice", e, 0, e, e);
            var h = fx.Make(huge);
            var t = fx.Make(twice, h, h);
            var tt = fx.Make(twice, t, t);

            Assert.Equal(long.MaxValue / 2 + 1, fx.Extract(e, h)?.Cost);
            Assert.Equal(long.MaxValue, fx.Extract(e, t)?.Cost);
            Assert.Equal(long.MaxValue, fx.Extract(e, tt)?.Cost);
            Assert.Equal(long.MaxValue, fx.Extract(e, tt, CostModel.Default.WithBaseValueCost(fx.I64, long.MaxValue))?.Cost);
        }

        [Fact]
        public void NegativeCostsAreRejected()
        {
            var fx = new ExtractionFixture();
            var e = new EqSort("E");
            var num = fx.Constructor("Num", e, 1, fx.I64);
            var vec = new TestVecSort("VecI", fx.I64);

            Assert.Throws<ArgumentOutOfRangeException>(() => CostModel.Default.WithFunctionCost(num, -1));
            Assert.Throws<ArgumentOutOfRangeException>(() => CostModel.Default.WithBaseValueCost(fx.I64, -1));
            Assert.Throws<ArgumentOutOfRangeException>(() => CostModel.Default.WithDefaultBaseValueCost(-1));
            Assert.Throws<ArgumentOutOfRangeException>(() => CostModel.Default.WithContainerCost(vec, -1));
            Assert.Throws<ArgumentException>(() => CostModel.Default.WithBaseValueCost(e, 1));
            Assert.Throws<ArgumentException>(() => CostModel.Default.WithContainerCost(fx.I64, 1));
            Assert.Throws<ArgumentOutOfRangeException>(() => fx.Constructor("Bad", e, -2));

            // A cost function that returns a negative cost fails the extraction that calls it.
            fx.Make(num, fx.Int(5));
            var negativeRow = CostModel.Default.WithNodeCost(num, (_, _) => -1);
            Assert.Throws<InvalidOperationException>(() => Extractor.ComputeCostsFromRootSorts(fx.Schema, [e], negativeRow));
            var negativeValue = CostModel.Default.WithBaseValueCost(fx.I64, (_, _) => -3);
            Assert.Throws<InvalidOperationException>(() => Extractor.ComputeCostsFromRootSorts(fx.Schema, [e], negativeValue));

            // On demand, the failure surfaces at the query, and leaves the extractor unusable.
            var onDemand = Extractor.CreateOnDemand(fx.Schema, negativeRow);
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
            Assert.Equal(3, fx.Extract(e, fx.Get(a))?.Cost);
            Assert.Equal(9, fx.Extract(e, fx.Get(a), changed)?.Cost);
        }

    }

}

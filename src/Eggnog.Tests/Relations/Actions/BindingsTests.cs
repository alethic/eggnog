using System;
using System.Linq;

using Xunit;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// Tests for <see cref="Bindings"/>: egglog's <c>Bindings</c> (core-relations/src/action/mod.rs:105-280).
    /// </summary>
    public class BindingsTests
    {

        static Variable V(int i) => new(i);

        static DenseIdMap<Variable, Value> Map(params (int Var, uint Value)[] entries)
        {
            var map = new DenseIdMap<Variable, Value>();
            foreach (var (v, value) in entries)
                map.Insert(V(v), new Value(value));

            return map;
        }

        [Fact]
        public void InsertFixesTheMatchCountAndPadsWithStale()
        {
            var b = new Bindings(8);
            Assert.Equal(0, b.IdCount);
            b.Insert(V(2), [1, 2, 3]);
            Assert.Equal(3, b.Matches);
            Assert.Equal(3, b.IdCount);
            Assert.Equal(new uint[] { 1, 2, 3 }, b[V(2)].ToArray());

            // The segment is padded with stale cells to the batch size.
            var offset = b.OffsetOf(V(2));
            Assert.Equal(Enumerable.Repeat(Value.StaleRep, 5), b.Data.Skip(offset + 3).Take(5));

            b.Insert(V(0), [4, 5, 6]);
            Assert.Equal(new uint[] { 4, 5, 6 }, b[V(0)].ToArray());
            Assert.Equal(-1, b.OffsetOf(V(1)));
            Assert.False(b.Contains(V(1)));
            Assert.False(b.TryGet(V(1), out _));
            Assert.Throws<InvalidOperationException>(() => b[V(1)].ToArray());
            Assert.Throws<ArgumentException>(() => b.Insert(V(1), [1, 2]));
            Assert.Throws<ArgumentException>(() => new Bindings(2).Insert(V(0), [1, 2, 3]));
            Assert.Throws<ArgumentOutOfRangeException>(() => b.Insert(Variable.Placeholder, [1, 2, 3]));
            Assert.Equal("{v0: [4, 5, 6], v2: [1, 2, 3]}", b.ToString());
        }

        [Fact]
        public void RedefiningAVariableTakesANewSegment()
        {
            var b = new Bindings(4);
            b.Insert(V(0), [1, 2]);
            var first = b.OffsetOf(V(0));
            b.Insert(V(0), [3, 4]);
            Assert.NotEqual(first, b.OffsetOf(V(0)));
            Assert.Equal(new uint[] { 3, 4 }, b[V(0)].ToArray());
        }

        [Fact]
        public void FirstPushMapsEveryBoundVariableAndLaterPushesOnlyUsedOnes()
        {
            var b = new Bindings();
            b.Push(Map((0, 10), (1, 11), (3, 13)), [V(0), V(3)]);
            Assert.Equal(1, b.Matches);
            Assert.Equal(4, b.IdCount);
            Assert.True(b.Contains(V(1)));

            b.Push(Map((0, 20), (1, 21), (3, 23)), [V(0), V(3)]);
            b.Push(Map((0, 30), (1, 31), (3, 33)), [V(0), V(3)]);
            Assert.Equal(3, b.Matches);
            Assert.Equal(new uint[] { 10, 20, 30 }, b[V(0)].ToArray());
            Assert.Equal(new uint[] { 13, 23, 33 }, b[V(3)].ToArray());

            // A bound variable the action does not use keeps only its first value, as in egglog.
            Assert.Equal(new uint[] { 11, Value.StaleRep, Value.StaleRep }, b[V(1)].ToArray());
        }

        [Fact]
        public void DensePushMatchesTheMapPush()
        {
            var dense = new Bindings();
            var arrays = new Bindings();
            var sparse = new Bindings();
            var values = new uint[4];
            Variable[] bound = [V(2), V(0)];
            Variable[] used = [V(0), V(2)];
            for (var row = 0; row < 5; row++)
            {
                values[0] = (uint)row;
                values[2] = (uint)(row * 7);
                dense.Push(values.AsSpan(), bound.AsSpan(), used.AsSpan());
                arrays.Push(values, bound, used);
                sparse.Push(Map((0, (uint)row), (2, (uint)(row * 7))), used);
            }

            Assert.Equal(sparse[V(0)].ToArray(), dense[V(0)].ToArray());
            Assert.Equal(sparse[V(2)].ToArray(), dense[V(2)].ToArray());
            Assert.Equal(sparse[V(0)].ToArray(), arrays[V(0)].ToArray());
            Assert.Equal(sparse[V(2)].ToArray(), arrays[V(2)].ToArray());
            Assert.Equal(5, dense.Matches);
        }

        [Fact]
        public void PushChecksTheBatchAndTheUsedVariables()
        {
            var b = new Bindings(2);
            b.Push(Map((0, 1)), [V(0)]);
            b.Push(Map((0, 2)), [V(0)]);
            Assert.Throws<InvalidOperationException>(() => b.Push(Map((0, 3)), [V(0)]));

            var c = new Bindings();
            Assert.Throws<InvalidOperationException>(() => c.Push(Map((0, 1)), [V(1)]));

            var d = new Bindings();
            d.Push(Map((0, 1)), [V(0)]);
            Assert.Throws<InvalidOperationException>(() => d.Push(Map((1, 1)), [V(0)]));
        }

        [Fact]
        public void LaterPushesRejectAUsedVariableWithNoSegment()
        {
            // Variable 1 lies inside the mapped range and variable 7 past it; neither has a segment after the first push.
            foreach (var missing in new[] { V(1), V(7) })
            {
                var b = new Bindings();
                b.Push(Map((0, 1), (2, 5)), [V(0)]);
                Assert.Throws<InvalidOperationException>(() => b.Push(Map((0, 2), (1, 9), (7, 9)), [V(0), missing]));
                Assert.Equal(1, b.Matches);
                Assert.Equal(new uint[] { 1 }, b[V(0)].ToArray());
                Assert.Equal(new uint[] { 5 }, b[V(2)].ToArray());

                var values = new uint[8];
                values[0] = 1;
                values[2] = 5;
                var dense = new Bindings();
                dense.Push(values.AsSpan(), [V(0), V(2)], [V(0)]);
                values[0] = 2;
                values[1] = 9;
                values[7] = 9;
                Assert.Throws<InvalidOperationException>(() => dense.Push(values.AsSpan(), [V(0), V(2)], [V(0), missing]));
                Assert.Equal(1, dense.Matches);
                Assert.Equal(new uint[] { 1 }, dense[V(0)].ToArray());
                Assert.Equal(new uint[] { 5 }, dense[V(2)].ToArray());
            }
        }

        [Fact]
        public void PushesWithoutVariablesOnlyCount()
        {
            var b = new Bindings();
            var empty = new DenseIdMap<Variable, Value>();
            for (var i = 0; i < 10; i++)
                b.Push(empty, ReadOnlySpan<Variable>.Empty);

            Assert.Equal(10, b.Matches);
            Assert.Equal(0, b.IdCount);
        }

        [Fact]
        public void ClearResetsEverythingAndReusesTheArrays()
        {
            var b = new Bindings(4);
            b.Push(Map((5, 1)), [V(5)]);
            b.Insert(V(6), [9]);
            var data = b.Data;
            b.Clear();
            Assert.Equal(0, b.Matches);
            Assert.Equal(0, b.IdCount);
            Assert.False(b.Contains(V(5)));
            Assert.False(b.Contains(V(6)));

            b.Push(Map((5, 2)), [V(5)]);
            Assert.Same(data, b.Data);
            Assert.Equal(0, b.OffsetOf(V(5)));
            Assert.Equal(new uint[] { 2 }, b[V(5)].ToArray());
        }

        [Fact]
        public void AllocateColumnGivesAStaleSegment()
        {
            var b = new Bindings(4);
            b.Insert(V(0), [1, 2, 3]);
            var offset = b.AllocateColumn(V(1));
            Assert.Equal(offset, b.OffsetOf(V(1)));
            Assert.Equal(Enumerable.Repeat(Value.StaleRep, 4), b.Data.Skip(offset).Take(4));
            Assert.Equal(3, b.Matches);
        }

        [Fact]
        public void MatchesIsBounded()
        {
            var b = new Bindings(4);
            b.Matches = 4;
            Assert.Throws<ArgumentOutOfRangeException>(() => b.Matches = 5);
            Assert.Throws<ArgumentOutOfRangeException>(() => new Bindings(0));
            Assert.Throws<ArgumentOutOfRangeException>(() => new Bindings(Mask.MaxLanes + 1));
            Assert.Equal(Mask.MaxLanes, Bindings.DefaultBatchSize);
        }

        [Fact]
        public void ManySegmentsGrowTheData()
        {
            var b = new Bindings();
            for (var v = 0; v < 40; v++)
                b.Insert(V(v), Enumerable.Range(v, 10).Select(x => (uint)x).ToArray());

            for (var v = 0; v < 40; v++)
                Assert.Equal(Enumerable.Range(v, 10).Select(x => (uint)x), b[V(v)].ToArray());
        }

    }

}

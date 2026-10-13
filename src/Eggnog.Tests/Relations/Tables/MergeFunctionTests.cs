using System;

using Xunit;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// Tests for the built-in <see cref="MergeFunction"/>s: what each returns, writes and stages, and the lattice flag
    /// each declares.
    /// </summary>
    public class MergeFunctionTests
    {

        static uint[] R(params uint[] cells) => cells;

        static ColumnId C(int column) => new(column);

        static (bool Changed, uint[] Output) Run(MergeFunction merge, ExecutionState state, uint[] current, uint[] incoming)
        {
            var output = new uint[current.Length];
            return (merge.Merge(state, current, incoming, output), output);
        }

        [Fact]
        public void KeepCurrentNeverChanges()
        {
            var state = TableTestUtil.EmptyState();
            Assert.False(KeepCurrentMerge.Instance.IsLattice);
            Assert.False(Run(KeepCurrentMerge.Instance, state, R(1, 2, 3), R(1, 9, 4)).Changed);
        }

        [Fact]
        public void KeepIncomingChangesOnlyWhenTheValueDiffers()
        {
            var state = TableTestUtil.EmptyState();
            var merge = new KeepIncomingMerge(C(1));
            Assert.False(merge.IsLattice);
            Assert.False(Run(merge, state, R(1, 2, 3), R(1, 2, 4)).Changed);
            var (changed, output) = Run(merge, state, R(1, 2, 3), R(1, 9, 4));
            Assert.True(changed);
            Assert.Equal(R(1, 9, 4), output);
        }

        [Fact]
        public void MinAndMaxKeepTheExtremeValue()
        {
            var state = TableTestUtil.EmptyState();
            var min = new MinMerge(C(1));
            var max = new MaxMerge(C(1));
            Assert.True(min.IsLattice);
            Assert.True(max.IsLattice);

            Assert.False(Run(min, state, R(1, 5, 1), R(1, 5, 2)).Changed);
            Assert.False(Run(min, state, R(1, 5, 1), R(1, 6, 2)).Changed);
            var (changed, output) = Run(min, state, R(1, 5, 1), R(1, 4, 2));
            Assert.True(changed);
            Assert.Equal(R(1, 4, 2), output);
            Assert.False(Run(max, state, R(1, 5, 1), R(1, 4, 2)).Changed);
            (changed, output) = Run(max, state, R(1, 5, 1), R(1, uint.MaxValue - 1, 2));
            Assert.True(changed);
            Assert.Equal(R(1, uint.MaxValue - 1, 2), output);
        }

        [Fact]
        public void AssertEqualReportsConflictsAndKeepsTheRow()
        {
            var state = TableTestUtil.EmptyState();
            var merge = new AssertEqualMerge(C(1), "f");
            Assert.True(merge.IsLattice);
            Assert.False(Run(merge, state, R(1, 5, 1), R(1, 5, 2)).Changed);
            Assert.Null(state.Failure);
            Assert.False(Run(merge, state, R(1, 5, 1), R(1, 6, 2)).Changed);
            Assert.Contains("function f", state.Failure);
            Assert.True(state.ShouldStop);
        }

        [Fact]
        public void UnionIdStagesTheUnionAndKeepsTheSmallerId()
        {
            var catalog = new TableCatalog();
            var uf = new DisplacedTable();
            var ufId = catalog.Add(uf);
            var state = new ExecutionState(catalog, 42);
            var withColumn = new UnionIdMerge(C(1), ufId, C(2));
            var withState = new UnionIdMerge(C(1), ufId);
            Assert.True(withColumn.IsLattice);
            Assert.Equal(-1, withState.TimestampColumn);

            Assert.False(Run(withColumn, state, R(1, 5, 1), R(1, 5, 2)).Changed);
            Assert.False(state.Changed);

            // Incoming larger: no row change, but the union is staged.
            Assert.False(Run(withColumn, state, R(1, 5, 1), R(1, 8, 3)).Changed);
            Assert.True(state.Changed);

            // Incoming smaller: the row changes to the incoming one.
            var (changed, output) = Run(withState, state, R(2, 7, 1), R(2, 6, 9));
            Assert.True(changed);
            Assert.Equal(R(2, 6, 9), output);

            state.Flush();
            uf.Merge(state);
            Assert.Equal(5u, uf.FindNaive(8));
            Assert.Equal(6u, uf.FindNaive(7));
            Assert.Equal(R(8, 5, 3), uf.GetRow(R(8))!.Value.Values);
            Assert.Equal(R(7, 6, 42), uf.GetRow(R(7))!.Value.Values);
        }

        [Fact]
        public void ConstructorsRejectNegativeColumns()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new KeepIncomingMerge(C(-1)));
            Assert.Throws<ArgumentOutOfRangeException>(() => new MinMerge(C(-1)));
            Assert.Throws<ArgumentOutOfRangeException>(() => new MaxMerge(C(-1)));
            Assert.Throws<ArgumentOutOfRangeException>(() => new AssertEqualMerge(C(-1)));
            Assert.Throws<ArgumentOutOfRangeException>(() => new UnionIdMerge(C(-1), new TableId(0)));
            Assert.Throws<ArgumentOutOfRangeException>(() => new UnionIdMerge(C(1), new TableId(0), C(-1)));
        }

    }

}

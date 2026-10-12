using Xunit;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// Review probe for <see cref="ExecutionState.VisibleRowCount"/> (colored-contexts spec §6.5): a child that asserts
    /// nothing counts what its parent counts (context = copy), for the union-find as for any table.
    /// </summary>
    public class VisibleRowCountReviewTests
    {

        [Fact]
        public void AnUntouchedChildCountsTheUnionFindRowsItSees()
        {
            var store = new Store(ParallelHeuristics.Serial);
            var root = store.Root;
            var a = root.NewId();
            var b = root.NewId();
            root.Union(a, b);
            root.Rebuild();
            root.Freeze();
            var child = root.CreateChild();
            var uf = store.UnionFindTable;

            // The child sees the root's displacement row...
            var row = new uint[3];
            Assert.True(child.TryGetRow(uf, [b], row));
            Assert.Equal(a, row[1]);

            // ...so it counts it, as the root does.
            var rootCount = root.Database.CreateExecutionState().VisibleRowCount(uf);
            Assert.Equal(1, rootCount);
            Assert.Equal(rootCount, child.Database.CreateExecutionState().VisibleRowCount(uf));
        }

    }

}

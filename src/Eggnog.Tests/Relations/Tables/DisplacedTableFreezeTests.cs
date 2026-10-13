using System;
using System.Linq;

using Xunit;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// Tests for freezing the root's union-find table: every path compressed, staged unions refused before and after,
    /// merges and clears refused, sealed staging, and reads unchanged.
    /// </summary>
    public class DisplacedTableFreezeTests
    {

        static DisplacedTable Chain()
        {
            var table = new DisplacedTable();
            for (var i = 30; i > 0; i -= 2)
                TableTestUtil.Stage(table, [(uint)i, (uint)(i - 2), 1]);

            table.Merge(TableTestUtil.EmptyState());
            return table;
        }

        [Fact]
        public void FreezeCompressesEveryPathAndKeepsEveryAnswer()
        {
            var table = Chain();
            var before = Enumerable.Range(0, 40).Select(i => table.FindNaive((uint)i)).ToArray();
            var rows = TableTestUtil.LiveRows(table).Select(TableTestUtil.Show).ToArray();
            Assert.False(table.IsFrozen);
            Assert.False(table.HasPendingUnions);

            table.Freeze();
            Assert.True(table.IsFrozen);
            var parents = table.UnionFind.UnionFind.Parents;
            for (var i = 0; i < parents.Length; i++)
                Assert.Equal(parents[(int)parents[i]], parents[i]);

            Assert.Equal(before, Enumerable.Range(0, 40).Select(i => table.FindNaive((uint)i)).ToArray());
            Assert.Equal(rows, TableTestUtil.LiveRows(table).Select(TableTestUtil.Show).ToArray());
            Assert.True(table.TryGetColumn(30, 1, out var canonical));
            Assert.Equal(0u, canonical);

            // A second freeze does nothing.
            table.Freeze();
            Assert.True(table.IsFrozen);
        }

        [Fact]
        public void AFrozenTableRefusesWrites()
        {
            var table = Chain();
            table.Freeze();
            Assert.Equal("The union-find of a frozen context cannot be written.", Assert.Throws<InvalidOperationException>(() => table.Merge(TableTestUtil.EmptyState())).Message);
            Assert.Throws<InvalidOperationException>(() => table.Clear());

            // Staging is refused when the buffer publishes, and the buffer's storage is returned.
            var buffer = table.NewBuffer();
            buffer.StageInsert([50, 51, 2]);
            Assert.Equal("The context is frozen.", Assert.Throws<InvalidOperationException>(() => buffer.Flush()).Message);
            Assert.False(table.HasPendingUnions);
            Assert.Equal(15, table.Count);
        }

        [Fact]
        public void FreezingWithStagedUnionsThrows()
        {
            var table = Chain();
            TableTestUtil.Stage(table, [50, 51, 2]);
            Assert.True(table.HasPendingUnions);
            Assert.Throws<InvalidOperationException>(() => table.Freeze());
            Assert.False(table.IsFrozen);

            // Once merged it freezes.
            table.Merge(TableTestUtil.EmptyState());
            Assert.False(table.HasPendingUnions);
            table.Freeze();
            Assert.True(table.IsFrozen);
            Assert.Equal(50u, table.FindNaive(51));
        }

        [Fact]
        public void ACloneOfAFrozenTableIsWritable()
        {
            var table = Chain();
            table.Freeze();
            var clone = (DisplacedTable)table.Clone();
            Assert.False(clone.IsFrozen);
            TableTestUtil.Stage(clone, [50, 51, 2]);
            clone.Merge(TableTestUtil.EmptyState());
            Assert.Equal(50u, clone.FindNaive(51));
            Assert.Equal(51u, table.FindNaive(51));
        }

        [Fact]
        public void ASortedWritesTableFreezesWithTheStorageFreeze()
        {
            // A sorted-writes table freezes too: sealed, shared, and no longer clearable.
            var table = new SortedWritesTable(1, 2, null, [], new KeepCurrentMerge());
            table.Freeze();
            Assert.True(table.IsShared);
            var buffer = table.NewBuffer();
            buffer.StageInsert([1, 2]);
            Assert.Throws<InvalidOperationException>(() => buffer.Flush());
            Assert.Throws<InvalidOperationException>(() => table.Clear());
            table.Freeze();
        }

    }

}

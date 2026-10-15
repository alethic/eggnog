using System;
using System.Runtime.CompilerServices;

using Xunit;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// The storage sizes <see cref="SortedWritesTable"/> reports: the row buffer's capacity in bytes, the scratch
    /// buffer, empty until a parallel compaction swaps the old rows' storage into it, and the key index's slots and
    /// control bytes over every shard.
    /// </summary>
    public sealed class SortedWritesTableFootprintTests
    {

        /// <summary>
        /// Enough rows for the parallel paths, which start above 20000 rows.
        /// </summary>
        const int Large = 30_000;

        static ColumnId C(int column) => new(column);

        [Fact]
        public void TheRowStorageIsTheRowBuffersCapacityInBytes()
        {
            var state = TableTestUtil.EmptyState();
            var table = NewTable(ParallelHeuristics.Serial);
            Assert.Equal((long)table.Rows.Data.Length * sizeof(uint), table.RowStorageBytes);

            Stage(table, 0, 1000, 1);
            table.Merge(state);

            Assert.Equal((long)table.Rows.Data.Length * sizeof(uint), table.RowStorageBytes);
            Assert.True(table.RowStorageBytes >= 1000L * 3 * sizeof(uint));
            Assert.Equal(0, table.ScratchStorageBytes);
        }

        [Fact]
        public void AParallelCompactionLeavesTheOldRowStorageInTheScratchBuffer()
        {
            var state = TableTestUtil.EmptyState();
            var parallel = NewTable(new ParallelHeuristics(4));
            var serial = NewTable(ParallelHeuristics.Serial);
            foreach (var table in new[] { parallel, serial })
            {
                Stage(table, 0, Large, 1);
                table.Merge(state);
                Stage(table, Large, Large, 2);
                table.Merge(state);
            }

            var before = parallel.RowStorageBytes;
            Assert.Equal(0, parallel.ScratchStorageBytes);

            // Removing two thirds of the rows compacts both tables, the first in parallel.
            foreach (var table in new[] { parallel, serial })
            {
                var removals = table.NewBuffer();
                for (uint k = 0; k < 2 * Large; k++)
                {
                    if (k % 3 != 0)
                        removals.StageRemove(new[] { k });
                }

                removals.Flush();
                table.Merge(state);
                Assert.Equal(1, table.Version.Major);
            }

            Assert.Equal(before, parallel.ScratchStorageBytes);
            Assert.Equal((long)parallel.Rows.Data.Length * sizeof(uint), parallel.RowStorageBytes);
            Assert.Equal(0, serial.ScratchStorageBytes);
        }

        [Fact]
        public void TheKeyIndexOfOneShardIsItsSlotsAndTheirControlBytes()
        {
            var state = TableTestUtil.EmptyState();
            var table = NewTable(ParallelHeuristics.Serial);
            Stage(table, 0, 1000, 1);
            table.Merge(state);

            // One shard of 2^k slots: an entry and a control byte per slot, and a trailing copy of the first group of
            // control bytes.
            var perSlot = Unsafe.SizeOf<TableEntry>() + 1;
            var slotBytes = table.KeyIndexBytes - ControlGroup.Width;
            Assert.Equal(0, slotBytes % perSlot);
            var slots = slotBytes / perSlot;
            Assert.True(slots >= 1000);
            Assert.True(long.IsPow2(slots));
        }

        [Fact]
        public void TheKeyIndexCountsAnEntryAndAControlByteForEveryKeyOfEveryShard()
        {
            var state = TableTestUtil.EmptyState();
            var table = NewTable(new ParallelHeuristics(4));
            var perKey = Unsafe.SizeOf<TableEntry>() + 1;

            Stage(table, 0, 100, 1);
            table.Merge(state);
            var small = table.KeyIndexBytes;
            Assert.True(small >= 100L * perKey);

            Stage(table, 100, Large, 2);
            table.Merge(state);
            Assert.True(table.KeyIndexBytes >= (Large + 100L) * perKey);
            Assert.True(table.KeyIndexBytes > small);
        }

        /// <summary>
        /// Creates a table of one key column, a value and a timestamp column it is sorted by, whose new value wins.
        /// </summary>
        static SortedWritesTable NewTable(ParallelHeuristics parallelism) =>
            new(1, 3, C(2), ReadOnlySpan<ColumnId>.Empty, ClosureMerge.TakeNew, parallelism);

        /// <summary>
        /// Stages <paramref name="count"/> rows with keys from <paramref name="first"/> and the timestamp
        /// <paramref name="ts"/>, and flushes them.
        /// </summary>
        static void Stage(SortedWritesTable table, int first, int count, uint ts)
        {
            var buffer = table.NewBuffer();
            for (var i = 0; i < count; i++)
                buffer.StageInsert(new[] { (uint)(first + i), (uint)i, ts });

            buffer.Flush();
        }

    }

}

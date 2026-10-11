using System;

using Xunit;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// Tests for <see cref="PendingState.Mark"/> and <see cref="PendingState.DiscardSince"/>, which let a run that throws
    /// discard what it staged (<see cref="Database.ExecuteRuleSet"/>).
    /// </summary>
    public class PendingStateRollbackTests
    {

        static uint[] R(params uint[] cells) => cells;

        [Fact]
        public void AnEmptyStateHasNoMark()
        {
            var state = new PendingState(new ShardData(4));
            Assert.True(state.IsEmpty);
            Assert.Null(state.Mark());

            var buffer = new MutationBuffer(state, 2, 1, true);
            buffer.StageInsert(R(1, 1));
            buffer.Flush();
            Assert.False(state.IsEmpty);
            Assert.NotNull(state.Mark());

            // Discarding since no mark drops everything, totals included.
            state.DiscardSince(null);
            Assert.True(state.IsEmpty);
            Assert.Equal(0, state.TotalRows);
        }

        [Fact]
        public void DiscardingKeepsWhatWasStagedBeforeTheMarkInOrder()
        {
            var table = new SortedWritesTable(1, 2, null, ReadOnlySpan<ColumnId>.Empty, ClosureMerge.TakeNew, ParallelHeuristics.Serial);
            var pending = table.Pending;
            var state = TableTestUtil.EmptyState();
            var before = table.NewBuffer();
            for (uint i = 0; i < 50; i++)
                before.StageInsert(R(i, 1));

            before.Flush();
            table.Merge(state);

            // Staged before the mark, in two flushes: kept.
            before.StageInsert(R(3, 2));
            before.Flush();
            before.StageRemove(R(4));
            before.Flush();
            var mark = pending.Mark();
            Assert.NotNull(mark);

            // Staged after it, in several buffers and shards: discarded.
            var after = table.NewBuffer();
            for (uint i = 0; i < 50; i++)
                after.StageInsert(R(i, 9));

            after.Flush();
            after.StageInsert(R(100, 9));
            after.StageRemove(R(5));
            after.Flush();
            Assert.Equal(52, pending.TotalRows);
            Assert.Equal(2, pending.TotalRemovals);

            pending.DiscardSince(mark);
            Assert.Equal(1, pending.TotalRows);
            Assert.Equal(1, pending.TotalRemovals);

            table.Merge(state);
            Assert.Equal(49, table.Count);
            var row = new uint[2];
            Assert.True(table.TryGetRow(R(3), row, out _));
            Assert.Equal(2u, row[1]);
            Assert.True(table.TryGetRow(R(5), row, out _));
            Assert.Equal(1u, row[1]);
            Assert.False(table.TryGetRow(R(4), row, out _));
            Assert.False(table.TryGetRow(R(100), row, out _));
        }

        [Fact]
        public void TheUnionFindTableExposesItsPendingWrites()
        {
            var table = new DisplacedTable();
            var buffer = table.NewBuffer();
            buffer.StageInsert(R(1, 2, 0));
            buffer.Flush();
            var mark = table.Pending.Mark();
            buffer.StageInsert(R(3, 4, 0));
            buffer.Flush();

            table.Pending.DiscardSince(mark);
            Assert.Equal(1, table.Pending.TotalRows);
        }

        [Fact]
        public void AMarkOfAnotherLayoutIsRejected()
        {
            var state = new PendingState(new ShardData(4));
            Assert.Throws<ArgumentException>(() => state.DiscardSince(new int[3]));
        }

    }

}

using System;
using System.Linq;

using Xunit;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// Tests for <see cref="MutationBuffer"/> and <see cref="PendingState"/>: pre-sharded staging, explicit flushing,
    /// fresh handles, and the pending queues a merge drains.
    /// </summary>
    public class MutationBufferTests
    {

        static uint[] R(params uint[] cells) => cells;

        [Fact]
        public void StagingPreShardsRowsByKeyHash()
        {
            var state = new PendingState(new ShardData(8));
            var buffer = new MutationBuffer(state, 3, 2, true);
            Assert.True(buffer.IsEmpty);
            for (uint i = 0; i < 200; i++)
                buffer.StageInsert(R(i, i * 7, 1));

            Assert.False(buffer.IsEmpty);
            Assert.Equal(0, state.TotalRows);
            buffer.Flush();
            Assert.True(buffer.IsEmpty);
            Assert.Equal(200, state.TotalRows);

            var seen = 0;
            for (var shard = 0; shard < state.ShardCount; shard++)
            {
                foreach (var rows in state.RowQueue(shard))
                {
                    foreach (var row in rows.EnumerateRows())
                    {
                        Assert.Equal(shard, state.ShardData.ShardOf(TableKeyHash.Hash(row, 2)));
                        seen++;
                    }
                }
            }

            Assert.Equal(200, seen);
            Assert.Equal(200, state.TakeTotalRows());
            Assert.Equal(0, state.TotalRows);
        }

        [Fact]
        public void RemovalsAreShardedAndCountedSeparately()
        {
            var state = new PendingState(new ShardData(4));
            var buffer = new MutationBuffer(state, 3, 2, true);
            buffer.StageRemove(R(1, 2));
            buffer.StageRemove(new uint[] { 9, 3, 4 }.AsSpan(1));
            buffer.Flush();
            Assert.Equal(0, state.TotalRows);
            Assert.Equal(2, state.TotalRemovals);
            var keys = Enumerable.Range(0, state.ShardCount).SelectMany(s => state.RemovalQueue(s)).Sum(b => b.Count);
            Assert.Equal(2, keys);
            Assert.Equal(2, state.TakeTotalRemovals());
        }

        [Fact]
        public void EmptyKeysAreCounted()
        {
            var state = new PendingState(new ShardData(1));
            var buffer = new MutationBuffer(state, 1, 0, true);
            buffer.StageRemove(ReadOnlySpan<uint>.Empty);
            buffer.StageRemove(ReadOnlySpan<uint>.Empty);
            buffer.Flush();
            Assert.True(state.RemovalQueue(0).TryDequeue(out var keys));
            Assert.Equal(2, keys.Count);
            Assert.Equal(0, keys.Arity);
        }

        [Fact]
        public void StagingValidatesShapes()
        {
            var state = new PendingState(new ShardData(1));
            var buffer = new MutationBuffer(state, 3, 1, true);
            Assert.StartsWith("Expected 3 cells but got 2.", Assert.Throws<ArgumentException>(() => buffer.StageInsert(R(1, 2))).Message);
            Assert.StartsWith("Expected 1 cells but got 2.", Assert.Throws<ArgumentException>(() => buffer.StageRemove(R(1, 2))).Message);
            buffer.StageInsert(new uint[] { 0, 1, 2, 3 }.AsSpan(1));
            buffer.Flush();
            Assert.True(state.RowQueue(0).TryDequeue(out var rows));
            Assert.Equal(R(1, 2, 3), rows.GetRow(0).ToArray());

            var noDelete = new MutationBuffer(state, 3, 1, false);
            Assert.Throws<InvalidOperationException>(() => noDelete.StageRemove(R(1)));
            Assert.Throws<ArgumentOutOfRangeException>(() => new MutationBuffer(state, 0, 0, true));
            Assert.Throws<ArgumentOutOfRangeException>(() => new MutationBuffer(state, 2, 3, true));
        }

        [Fact]
        public void FreshHandlesShareTheTableButNotTheStagedRows()
        {
            var state = TableTestUtil.EmptyState();
            var table = new SortedWritesTable(1, 2, null, ReadOnlySpan<ColumnId>.Empty, ClosureMerge.TakeNew, ParallelHeuristics.Serial);
            var first = table.NewBuffer();
            first.StageInsert(R(1, 1));
            var second = first.FreshHandle();
            Assert.True(second.IsEmpty);
            Assert.Equal(first.Arity, second.Arity);
            Assert.Equal(first.KeyCount, second.KeyCount);
            second.StageInsert(R(2, 2));
            second.Flush();
            table.Merge(state);
            Assert.Equal(1, table.Count);

            first.Flush();
            first.Flush();
            table.Merge(state);
            Assert.Equal(2, table.Count);

            // A flushed buffer can be reused.
            first.StageInsert(R(3, 3));
            first.Flush();
            table.Merge(state);
            Assert.Equal(3, table.Count);
        }

        [Fact]
        public void DeepCopyCopiesQueuesAndTotalsWithoutDrainingThem()
        {
            var state = new PendingState(new ShardData(2));
            var buffer = new MutationBuffer(state, 2, 1, true);
            for (uint i = 0; i < 10; i++)
                buffer.StageInsert(R(i, i));

            buffer.StageRemove(R(3));
            buffer.Flush();

            var copy = state.DeepCopy();
            Assert.Equal(10, copy.TotalRows);
            Assert.Equal(1, copy.TotalRemovals);
            for (var shard = 0; shard < 2; shard++)
            {
                Assert.Equal(state.RowQueue(shard).Sum(b => b.Count), copy.RowQueue(shard).Sum(b => b.Count));
                Assert.Equal(state.RemovalQueue(shard).Sum(b => b.Count), copy.RemovalQueue(shard).Sum(b => b.Count));
                Assert.All(state.RowQueue(shard).Zip(copy.RowQueue(shard)), pair => Assert.NotSame(pair.First, pair.Second));
            }

            copy.Clear();
            Assert.Equal(0, copy.TotalRows);
            Assert.Equal(0, copy.TotalRemovals);
            Assert.True(copy.RowQueue(0).IsEmpty && copy.RowQueue(1).IsEmpty);
            Assert.Equal(10, state.TotalRows);
            Assert.Equal(10, Enumerable.Range(0, 2).Sum(s => state.RowQueue(s).Sum(b => b.Count)));
        }

    }

}

using System;
using System.Threading;
using System.Threading.Tasks;

using Xunit;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// Lazy pending queues (colored-contexts spec §14.2, §21.9, M10.4): a child's tables create each queue on its first
    /// publication; until then readers see an empty shared queue, and an eager state (every root and bare table) is
    /// unchanged.
    /// </summary>
    public class LazyPendingStateTests
    {

        static uint[] R(params uint[] cells) => cells;

        [Fact]
        public void LazyQueuesStayEmptyAndSharedUntilFirstPublication()
        {
            var a = new PendingState(new ShardData(1), lazyQueues: true);
            var b = new PendingState(new ShardData(1), lazyQueues: true);
            Assert.True(a.IsEmpty);
            Assert.Null(a.Mark());
            Assert.Same(a.RowQueue(0), b.RowQueue(0));
            Assert.Same(a.RemovalQueue(0), b.RemovalQueue(0));
            Assert.False(a.RowQueue(0).TryDequeue(out _));

            var buffer = new MutationBuffer(a, 2, 1, true);
            buffer.StageInsert(R(1, 1));
            buffer.Flush();
            Assert.NotSame(a.RowQueue(0), b.RowQueue(0));
            Assert.Same(a.RemovalQueue(0), b.RemovalQueue(0));
            Assert.Equal(1, a.TotalRows);
            Assert.True(b.IsEmpty);
            Assert.True(b.RowQueue(0).IsEmpty);

            buffer.StageRemove(R(1));
            buffer.Flush();
            Assert.NotSame(a.RemovalQueue(0), b.RemovalQueue(0));
            Assert.Equal(1, a.TotalRemovals);
            a.Clear();
            Assert.True(a.IsEmpty);
        }

        [Fact]
        public void ALazyStateIsSealedLikeAnEagerOne()
        {
            foreach (var lazy in new[] { true, false })
            {
                var state = new PendingState(new ShardData(1), lazy);
                state.Seal();
                Assert.True(state.IsSealed);
                var buffer = new MutationBuffer(state, 2, 1, true);
                buffer.StageInsert(R(1, 1));
                Assert.Throws<InvalidOperationException>(() => buffer.Flush());
                Assert.True(state.IsEmpty);
            }

            // Sealed after its queues exist, a lazy state still refuses.
            var used = new PendingState(new ShardData(1), lazyQueues: true);
            var first = new MutationBuffer(used, 2, 1, true);
            first.StageInsert(R(1, 1));
            first.Flush();
            first.StageRemove(R(1));
            first.Flush();
            used.Seal();
            first.StageInsert(R(2, 2));
            Assert.Throws<InvalidOperationException>(() => first.Flush());
        }

        [Fact]
        public void RollbackWorksAcrossTheFirstPublication()
        {
            var state = new PendingState(new ShardData(1), lazyQueues: true);
            var mark = state.Mark();
            var buffer = new MutationBuffer(state, 2, 1, true);
            buffer.StageInsert(R(1, 1));
            buffer.Flush();
            state.DiscardSince(mark);
            Assert.True(state.IsEmpty);
            Assert.Equal(0, state.TotalRows);

            buffer.StageInsert(R(2, 2));
            buffer.Flush();
            var kept = state.Mark();
            buffer.StageInsert(R(3, 3));
            buffer.Flush();
            state.DiscardSince(kept);
            Assert.Equal(1, state.TotalRows);
            Assert.Single(state.RowQueue(0));

            var copy = state.DeepCopy();
            Assert.Equal(1, copy.TotalRows);
        }

        /// <summary>
        /// Racing first publications agree on one queue per shard: every row arrives.
        /// </summary>
        [Fact]
        public void RacingFirstPublicationsAgreeOnOneQueue()
        {
            for (var round = 0; round < 20; round++)
            {
                var state = new PendingState(new ShardData(4), lazyQueues: true);
                const int Producers = 8;
                const int Rows = 64;
                using var start = new Barrier(Producers);
                Parallel.For(0, Producers, new ParallelOptions { MaxDegreeOfParallelism = Producers }, p =>
                {
                    var buffer = new MutationBuffer(state, 2, 1, true);
                    start.SignalAndWait();
                    for (uint i = 0; i < Rows; i++)
                        buffer.StageInsert(R((uint)p * Rows + i, 1));

                    buffer.Flush();
                });

                var count = 0;
                for (var shard = 0; shard < 4; shard++)
                {
                    while (state.RowQueue(shard).TryDequeue(out var rows))
                    {
                        count += rows.Count;
                        rows.Dispose();
                    }
                }

                Assert.Equal(Producers * Rows, count);
            }
        }

        [Fact]
        public void AnEagerStateHasItsOwnQueuesFromTheStart()
        {
            var a = new PendingState(new ShardData(2));
            var b = new PendingState(new ShardData(2));
            Assert.NotSame(a.RowQueue(0), b.RowQueue(0));
            Assert.NotSame(a.RowQueue(0), a.RowQueue(1));
            Assert.NotSame(a.RemovalQueue(1), b.RemovalQueue(1));
        }

        [Fact]
        public void ALazyNotificationListAllocatesItsFlagsOnFirstNotify()
        {
            var list = new NotificationList<TableId>(lazy: true);
            Assert.True(list.IsEmpty);
            Assert.Empty(list.Reset());
            list.Notify(new TableId(5));
            list.Notify(new TableId(5));
            list.Notify(new TableId(700));
            Assert.Equal([new TableId(5), new TableId(700)], list.Reset());
            list.Notify(new TableId(5));
            Assert.Equal([new TableId(5)], list.Reset());

            var far = new NotificationList<TableId>(lazy: true);
            far.Notify(new TableId(1000));
            far.Notify(new TableId(3));
            Assert.Equal([new TableId(1000), new TableId(3)], far.Reset());
            Assert.Throws<ArgumentOutOfRangeException>(() => new NotificationList<TableId>(lazy: true).Notify(new TableId(-1)));
        }

    }

}

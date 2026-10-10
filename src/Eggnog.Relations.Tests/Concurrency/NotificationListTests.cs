using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

using Xunit;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// Tests for <see cref="NotificationList{TKey}"/>: ports of egglog's concurrency/src/tests.rs:105-144, plus
    /// randomized comparisons against a reference and concurrent stress.
    /// </summary>
    public sealed class NotificationListTests
    {

        static TableId[] Ids(params int[] indexes) => indexes.Select(i => new TableId(i)).ToArray();

        [Fact]
        public void SingleThreaded()
        {
            var list = new NotificationList<TableId>();
            list.Notify(new TableId(0));
            list.Notify(new TableId(2));
            list.Notify(new TableId(5));
            Assert.Equal(Ids(0, 2, 5), list.Reset());

            list.Notify(new TableId(1));
            list.Notify(new TableId(3));
            Assert.Equal(Ids(1, 3), list.Reset());
        }

        [Fact]
        public void MultiThreaded()
        {
            for (var round = 0; round < 10; round++)
            {
                var list = new NotificationList<TableId>();
                var threads = ConcurrencyTestThread.StartMany(20, i =>
                {
                    list.Notify(new TableId(i * 100));
                    list.Notify(new TableId((i + 1) * 100));
                });
                ConcurrencyTestThread.JoinAll(threads);

                var notified = list.Reset();
                Array.Sort(notified, (a, b) => a.Rep.CompareTo(b.Rep));
                var expected = Enumerable.Range(0, 21).Select(i => new TableId(i * 100)).ToArray();
                Assert.Equal(expected.Length, notified.Length);
                Assert.Equal(expected, notified);
            }
        }

        [Fact]
        public void ResetOfEmptyListIsEmpty()
        {
            var list = new NotificationList<TableId>();
            Assert.Empty(list.Reset());
            list.Notify(new TableId(7));
            list.Notify(new TableId(7));
            Assert.Equal(Ids(7), list.Reset());
            Assert.Empty(list.Reset());
        }

        [Fact]
        public void ResetIntoBufferAppends()
        {
            var list = new NotificationList<TableId>();
            var buffer = new List<TableId> { new(99) };
            list.Notify(new TableId(4));
            list.Notify(new TableId(1));
            list.Notify(new TableId(4));
            Assert.Equal(2, list.Reset(buffer));
            Assert.Equal(Ids(99, 4, 1), buffer);

            // The flags were cleared: the same ids are reported again after a reset.
            list.Notify(new TableId(1));
            Assert.Equal(1, list.Reset(buffer));
            Assert.Equal(Ids(99, 4, 1, 1), buffer);
            Assert.Equal(0, list.Reset(buffer));
        }

        [Theory]
        [InlineData(1)]
        [InlineData(2)]
        [InlineData(3)]
        [InlineData(4)]
        [InlineData(5)]
        public void MatchesReferenceOnRandomSequences(int seed)
        {
            var random = new Random(seed);
            var list = new NotificationList<TableId>();
            var order = new List<TableId>();
            var seen = new HashSet<int>();
            var maxId = random.Next(1, 2000);
            for (var step = 0; step < 5000; step++)
            {
                if (random.Next(50) == 0)
                {
                    Assert.Equal(order, list.Reset());
                    order.Clear();
                    seen.Clear();
                }
                else
                {
                    var id = random.Next(maxId);
                    list.Notify(new TableId(id));
                    if (seen.Add(id))
                        order.Add(new TableId(id));
                }
            }

            Assert.Equal(order, list.Reset());
        }

        [Fact]
        public void ConcurrentNotifiersReportEachIdOnce()
        {
            // Many threads hammer overlapping ids, including ids that force the state vector to grow; every round must
            // report each distinct id exactly once.
            const int Threads = 8;
            var list = new NotificationList<TableId>();
            for (var round = 0; round < 20; round++)
            {
                var expected = new HashSet<int>();
                var perThread = new int[Threads][];
                var random = new Random(round);
                for (var t = 0; t < Threads; t++)
                {
                    perThread[t] = new int[2000];
                    for (var i = 0; i < perThread[t].Length; i++)
                    {
                        perThread[t][i] = random.Next(round * 300 + 64);
                        expected.Add(perThread[t][i]);
                    }
                }

                using var start = new Barrier(Threads);
                var threads = ConcurrencyTestThread.StartMany(Threads, t =>
                {
                    start.SignalAndWait();
                    foreach (var id in perThread[t])
                        list.Notify(new TableId(id));
                });
                ConcurrencyTestThread.JoinAll(threads);

                var reported = list.Reset();
                Assert.Equal(expected.Count, reported.Length);
                Assert.Equal(expected.OrderBy(x => x), reported.Select(x => x.Rep).OrderBy(x => x));
            }
        }

        [Fact]
        public void IdsAcrossSegmentBoundaries()
        {
            // The flags live in segments of 128, 128, 256, 512, ...; ids on either side of each boundary are distinct.
            var list = new NotificationList<TableId>();
            var ids = new List<int> { 0 };
            for (var boundary = 128; boundary <= 1 << 20; boundary <<= 1)
            {
                ids.Add(boundary - 1);
                ids.Add(boundary);
            }

            foreach (var id in ids)
                list.Notify(new TableId(id));

            foreach (var id in ids)
                list.Notify(new TableId(id));

            Assert.Equal(ids.Select(i => new TableId(i)), list.Reset());
            Assert.Empty(list.Reset());

            // Each flag was cleared: every id is reported again.
            var reversed = Enumerable.Reverse(ids).ToArray();
            foreach (var id in reversed)
                list.Notify(new TableId(id));

            Assert.Equal(reversed.Select(i => new TableId(i)), list.Reset());
        }

        [Fact]
        public void RejectsNegativeIds()
        {
            var list = new NotificationList<TableId>();
            Assert.Throws<ArgumentOutOfRangeException>(() => list.Notify(new TableId(-1)));
            Assert.Throws<ArgumentOutOfRangeException>(() => list.Notify(new TableId(int.MinValue)));
            Assert.Empty(list.Reset());
        }

        [Fact]
        public void GrowthRacingNotifiesLosesNoFlag()
        {
            // Every thread notifies every id of a fresh list in its own random order, so segments are allocated while
            // other threads are setting flags in them. A flag set during growth must not be lost (which would report an
            // id twice) and every id must be reported.
            const int Threads = 8;
            const int Ids = 20_000;
            for (var round = 0; round < 5; round++)
            {
                var list = new NotificationList<TableId>();
                var orders = new int[Threads][];
                for (var t = 0; t < Threads; t++)
                {
                    orders[t] = Enumerable.Range(0, Ids).ToArray();
                    new Random(round * Threads + t).Shuffle(orders[t]);
                }

                using var start = new Barrier(Threads);
                var threads = ConcurrencyTestThread.StartMany(Threads, t =>
                {
                    start.SignalAndWait();
                    foreach (var id in orders[t])
                        list.Notify(new TableId(id));
                });
                ConcurrencyTestThread.JoinAll(threads);

                var reported = list.Reset();
                Assert.Equal(Ids, reported.Length);
                Assert.Equal(Enumerable.Range(0, Ids), reported.Select(x => x.Rep).OrderBy(x => x));
            }
        }

    }

}

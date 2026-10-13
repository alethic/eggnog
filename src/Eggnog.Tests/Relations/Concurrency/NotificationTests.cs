using System;
using System.Threading;

using Xunit;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// Tests for <see cref="Notification"/>, ported from egglog's concurrency/src/tests.rs:20-103.
    /// </summary>
    public sealed class NotificationTests
    {

        [Fact]
        public void SingleThreaded()
        {
            var n = new Notification();
            n.Notify();
            n.Wait();
        }

        [Fact]
        public void WakesUpMultiple()
        {
            var n = new Notification();
            var counter = 0;
            var threads = ConcurrencyTestThread.StartMany(20, _ =>
            {
                n.Wait();
                Interlocked.Increment(ref counter);
            });

            Thread.Sleep(100);
            Assert.Equal(0, Volatile.Read(ref counter));
            n.Notify();
            ConcurrencyTestThread.JoinAll(threads);
            Assert.Equal(20, Volatile.Read(ref counter));
        }

        [Fact]
        public void TimesOut()
        {
            var n = new Notification();
            var threads = ConcurrencyTestThread.StartMany(20, _ => Assert.False(n.WaitWithTimeout(TimeSpan.FromMilliseconds(10))));
            ConcurrencyTestThread.JoinAll(threads);
        }

        [Fact]
        public void TimeoutObservesNotification()
        {
            var notified = new Notification();
            notified.Notify();
            Assert.True(notified.WaitWithTimeout(TimeSpan.FromMilliseconds(1)));

            var n = new Notification();
            var notifier = ConcurrencyTestThread.Start(() =>
            {
                Thread.Sleep(1);
                n.Notify();
            });

            Assert.True(n.WaitWithTimeout(TimeSpan.FromSeconds(1)));
            notifier.Join();
        }

        [Fact]
        public void Race()
        {
            var n = new Notification();
            var counter = 0;
            var threads = ConcurrencyTestThread.StartMany(20, i =>
            {
                if (i == 19)
                    n.Notify();
                else
                    n.Wait();

                Interlocked.Increment(ref counter);
            });

            ConcurrencyTestThread.JoinAll(threads);
            Assert.Equal(20, Volatile.Read(ref counter));
        }

        [Fact]
        public void HasBeenNotifiedTracksNotify()
        {
            var n = new Notification();
            Assert.False(n.HasBeenNotified);
            Assert.False(n.WaitWithTimeout(TimeSpan.Zero));
            n.Notify();
            Assert.True(n.HasBeenNotified);
            n.Notify();
            Assert.True(n.HasBeenNotified);
            Assert.True(n.WaitWithTimeout(TimeSpan.Zero));
            Assert.True(n.WaitWithTimeout(Timeout.InfiniteTimeSpan));
        }

        [Fact]
        public void TimeoutRejectsNegative()
        {
            var n = new Notification();
            Assert.Throws<ArgumentOutOfRangeException>(() => n.WaitWithTimeout(TimeSpan.FromMilliseconds(-5)));
        }

        [Fact]
        public void NotifyPublishesPriorWrites()
        {
            // A waiter that sees the notification must see everything the notifier wrote before notifying. One reader
            // races through many notifications, some already notified when it gets there and some not yet.
            const int Rounds = 2000;
            var notifications = new Notification[Rounds];
            var payloads = new int[Rounds][];
            for (var round = 0; round < Rounds; round++)
            {
                notifications[round] = new Notification();
                payloads[round] = new int[16];
            }

            var reader = ConcurrencyTestThread.Start(() =>
            {
                for (var round = 0; round < Rounds; round++)
                {
                    if (round % 2 == 0)
                        notifications[round].Wait();
                    else
                        while (notifications[round].HasBeenNotified == false)
                            Thread.SpinWait(1);

                    for (var i = 0; i < payloads[round].Length; i++)
                        Assert.Equal(round + i, payloads[round][i]);
                }
            });

            for (var round = 0; round < Rounds; round++)
            {
                for (var i = 0; i < payloads[round].Length; i++)
                    payloads[round][i] = round + i;

                notifications[round].Notify();
            }

            reader.Join();
        }

    }

}

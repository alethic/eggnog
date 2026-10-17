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

            // egglog notifies from a thread that sleeps 1 ms while this one waits with a 1 s timeout, so the test fails
            // whenever that thread is not run within the second. Here the wait runs on its own thread with a timeout of
            // a day, and this thread notifies once that thread is blocked or has ended. The waiting thread makes no
            // managed wait before the call and nothing else takes the notification's lock, so once blocked it is in its
            // timed wait. The wait must still be blocked when notified and must then return true within 30 s, where one
            // that does not observe the notification waits out the day.
            var n = new Notification();
            var calling = false;
            var result = false;
            var waiter = ConcurrencyTestThread.Start(() =>
            {
                Volatile.Write(ref calling, true);
                result = n.WaitWithTimeout(TimeSpan.FromDays(1));
            });

            var spin = new SpinWait();
            var deadline = Environment.TickCount64 + 30_000;
            while (Volatile.Read(ref calling) == false || (waiter.ThreadState & (ThreadState.WaitSleepJoin | ThreadState.Stopped)) == 0)
            {
                Assert.True(Environment.TickCount64 < deadline, "the wait neither blocked nor ended");
                spin.SpinOnce();
            }

            Assert.True((waiter.ThreadState & ThreadState.Stopped) == 0, "the wait ended before the notification");
            n.Notify();
            Assert.True(waiter.Join(TimeSpan.FromSeconds(30)), "the wait did not return after the notification");
            Assert.True(result);
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

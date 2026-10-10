using System;
using System.Threading;

using Xunit;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// Tests for <see cref="ResettableOnceLock{T}"/>: ports of egglog's concurrency/src/tests.rs:491-638, plus
    /// failure and multi-epoch concurrency tests.
    /// </summary>
    public sealed class ResettableOnceLockTests
    {

        [Fact]
        public void Basic()
        {
            var lockObj = new ResettableOnceLock<int>(0);

            // Initially, get should return nothing.
            Assert.False(lockObj.TryGet(out _));
            Assert.False(lockObj.IsCompleted);

            // The first call to GetOrUpdate runs the update.
            Assert.Equal(42, lockObj.GetOrUpdate(0, static (ref int x, int _) => x = 42));

            // Subsequent gets return the value.
            Assert.True(lockObj.TryGet(out var value));
            Assert.Equal(42, value);

            // GetOrUpdate returns the value without updating.
            Assert.Equal(42, lockObj.GetOrUpdate(0, static (ref int x, int _) => x = 100));
        }

        [Fact]
        public void Reset()
        {
            var lockObj = new ResettableOnceLock<int>(0);
            lockObj.GetOrUpdate(0, static (ref int x, int _) => x = 42);
            Assert.True(lockObj.TryGet(out var value));
            Assert.Equal(42, value);

            lockObj.Reset();
            Assert.False(lockObj.TryGet(out _));

            Assert.Equal(100, lockObj.GetOrUpdate(0, static (ref int x, int _) => x = 100));
            Assert.True(lockObj.TryGet(out value));
            Assert.Equal(100, value);
        }

        [Fact]
        public void ConcurrentReaders()
        {
            var lockObj = new ResettableOnceLock<int>(0);
            using var barrier = new Barrier(10);
            lockObj.GetOrUpdate(0, static (ref int x, int _) => x = 42);

            var threads = ConcurrencyTestThread.StartMany(10, _ =>
            {
                barrier.SignalAndWait();
                Assert.True(lockObj.TryGet(out var value));
                Assert.Equal(42, value);
            });
            ConcurrencyTestThread.JoinAll(threads);
        }

        [Fact]
        public void ConcurrentGetOrUpdate()
        {
            var lockObj = new ResettableOnceLock<int>(0);
            var counter = new int[1];
            using var barrier = new Barrier(10);

            var threads = ConcurrencyTestThread.StartMany(10, _ =>
            {
                barrier.SignalAndWait();

                // Only one thread runs the update.
                var value = lockObj.GetOrUpdate(counter, static (ref int x, int[] c) =>
                {
                    Interlocked.Increment(ref c[0]);
                    Thread.Sleep(10);
                    x = 42;
                });
                Assert.Equal(42, value);
            });
            ConcurrencyTestThread.JoinAll(threads);

            Assert.Equal(1, Volatile.Read(ref counter[0]));
            Assert.True(lockObj.TryGet(out var final));
            Assert.Equal(42, final);
        }

        [Fact]
        public void UpdateMutability()
        {
            var lockObj = new ResettableOnceLock<(int Value, bool Updated)>((0, false));
            lockObj.GetOrUpdate(0, static (ref (int Value, bool Updated) data, int _) =>
            {
                data.Value = 100;
                data.Updated = true;
            });

            Assert.True(lockObj.TryGet(out var result));
            Assert.Equal(100, result.Value);
            Assert.True(result.Updated);
        }

        [Fact]
        public void MultipleResets()
        {
            var lockObj = new ResettableOnceLock<int>(0);
            for (var i = 1; i <= 5; i++)
            {
                lockObj.GetOrUpdate(i, static (ref int x, int k) => x = k * 10);
                Assert.True(lockObj.TryGet(out var value));
                Assert.Equal(i * 10, value);
                lockObj.Reset();
                Assert.False(lockObj.TryGet(out _));
            }
        }

        [Fact]
        public void UsableFromAnotherThread()
        {
            // egglog's resettable_once_lock_send_sync moves the lock to another thread.
            var lockObj = new ResettableOnceLock<int>(42);
            var result = 0;
            var t = ConcurrencyTestThread.Start(() =>
            {
                lockObj.GetOrUpdate(0, static (ref int x, int _) => x += 1);
                Assert.True(lockObj.TryGet(out result));
            });
            t.Join();
            Assert.Equal(43, result);
        }

        [Fact]
        public void ThrowingUpdateLeavesTheLockToBeUpdated()
        {
            // call_once_force semantics: a failed update does not complete the lock, and the next caller retries.
            var lockObj = new ResettableOnceLock<int>(1);
            Assert.Throws<InvalidOperationException>(() => lockObj.GetOrUpdate(0, static (ref int x, int _) =>
            {
                x = 5;
                throw new InvalidOperationException("boom");
            }));
            Assert.False(lockObj.IsCompleted);
            Assert.False(lockObj.TryGet(out _));

            Assert.Equal(6, lockObj.GetOrUpdate(0, static (ref int x, int _) => x += 1));
            Assert.True(lockObj.IsCompleted);
        }

        [Fact]
        public void ReturnsReferenceToTheStoredValue()
        {
            var lockObj = new ResettableOnceLock<long[]>(new long[4]);
            ref readonly var stored = ref lockObj.GetOrUpdate(7L, static (ref long[] a, long v) => a[2] = v);
            Assert.Equal(7, stored[2]);
            Assert.True(lockObj.TryGet(out var again));
            Assert.Same(stored, again);
        }

        [Fact]
        public void EachEpochUpdatesExactlyOnceUnderContention()
        {
            // Every epoch, many threads race to get the value; exactly one update must run, and every thread must see
            // its result.
            const int Threads = 8;
            const int Epochs = 200;
            var lockObj = new ResettableOnceLock<int[]>(new int[1]);
            var updates = new int[1];
            using var start = new Barrier(Threads + 1);
            using var end = new Barrier(Threads + 1);
            var threads = ConcurrencyTestThread.StartMany(Threads, _ =>
            {
                for (var epoch = 1; epoch <= Epochs; epoch++)
                {
                    start.SignalAndWait();
                    var value = lockObj.GetOrUpdate(updates, static (ref int[] data, int[] u) =>
                    {
                        data[0] = Interlocked.Increment(ref u[0]);
                    });

                    // Check after the barrier so that a failure ends this thread instead of stranding the others.
                    var seen = value[0];
                    end.SignalAndWait();
                    Assert.Equal(epoch, seen);
                }
            });

            for (var epoch = 1; epoch <= Epochs; epoch++)
            {
                Assert.True(start.SignalAndWait(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken));
                Assert.True(end.SignalAndWait(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken));

                // Exclusive access between the barriers: reset for the next epoch.
                Assert.Equal(epoch, Volatile.Read(ref updates[0]));
                lockObj.Reset();
            }

            ConcurrencyTestThread.JoinAll(threads);
        }

        [Fact]
        public void ReentrantUpdateThrowsAndLeavesTheLockToBeUpdated()
        {
            // std::sync::Once deadlocks on a nested get_or_update; here the nested call is rejected, and the epoch's
            // update has not completed, so the next caller runs its own.
            var lockObj = new ResettableOnceLock<int>(0);
            var runs = 0;
            Assert.Throws<InvalidOperationException>(() => lockObj.GetOrUpdate(0, (ref int x, int _) =>
            {
                runs++;
                lockObj.GetOrUpdate(0, static (ref int y, int _) => y = -1);
            }));
            Assert.Equal(1, runs);
            Assert.False(lockObj.IsCompleted);

            // TryGet inside the update sees "to be updated", like the Rust's get.
            Assert.Equal(5, lockObj.GetOrUpdate(0, (ref int x, int _) =>
            {
                Assert.False(lockObj.TryGet(out _));
                x = 5;
            }));
            Assert.True(lockObj.IsCompleted);
        }

        [Fact]
        public void UpdatesOfOtherLocksMayNest()
        {
            // Only reentry into the same lock is rejected; an update may bring another lock up to date.
            var inner = new ResettableOnceLock<int>(0);
            var outer = new ResettableOnceLock<int>(0);
            var value = outer.GetOrUpdate(inner, static (ref int x, ResettableOnceLock<int> other) =>
            {
                x = other.GetOrUpdate(0, static (ref int y, int _) => y = 3) + 1;
            });
            Assert.Equal(4, value);
            Assert.True(inner.IsCompleted);
        }

    }

}

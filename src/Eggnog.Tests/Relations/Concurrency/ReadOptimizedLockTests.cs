using System;
using System.Collections.Generic;
using System.Threading;

using Xunit;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// Tests for <see cref="ReadOptimizedLock{T}"/>: ports of egglog's concurrency/src/tests.rs:146-207, plus stress
    /// tests of mutual exclusion, visibility and blocking.
    /// </summary>
    public sealed class ReadOptimizedLockTests
    {

        [Fact]
        public void SimpleMutex()
        {
            for (var round = 0; round < 50; round++)
            {
                var m = new ReadOptimizedLock<int>(0);
                var readGuard1 = m.Read();
                var readGuard2 = m.Read();
                Assert.Equal(0, readGuard1.Value);
                Assert.Equal(0, readGuard2.Value);

                var locked = new Notification();
                var writer = ConcurrencyTestThread.Start(() =>
                {
                    using var guard = m.Lock();
                    locked.Notify();
                    guard.Value = 5;
                });

                Thread.Sleep(1);
                Assert.False(locked.HasBeenNotified);
                readGuard1.Dispose();
                Thread.Sleep(1);
                Assert.False(locked.HasBeenNotified);
                readGuard2.Dispose();
                locked.Wait();
                writer.Join();

                using var read = m.Read();
                Assert.Equal(5, read.Value);
            }
        }

        [Fact]
        public void ConvenienceMethods()
        {
            var m = new ReadOptimizedLock<List<int>>(new List<int>());
            m.GetMutableRef().AddRange([1, 2, 3]);
            Assert.Equal(new[] { 1, 2, 3 }, m.IntoInner());

            var replaced = new ReadOptimizedLock<int[]>([1]);
            replaced.GetMutableRef() = [4, 5];
            Assert.Equal(new[] { 4, 5 }, replaced.IntoInner());
        }

        [Fact]
        public void WaitsForOngoingWriter()
        {
            var m = new ReadOptimizedLock<int>(0);
            var writer = m.Lock();
            var attempted = new Notification();
            var acquired = new Notification();

            var waiter = ConcurrencyTestThread.Start(() =>
            {
                attempted.Notify();
                using var guard = m.Lock();
                guard.Value = 1;
                acquired.Notify();
            });

            attempted.Wait();
            Thread.Sleep(1);
            Assert.False(acquired.HasBeenNotified);
            writer.Dispose();
            acquired.Wait();
            waiter.Join();

            using var read = m.Read();
            Assert.Equal(1, read.Value);
        }

        [Fact]
        public void ReaderWaitsForOngoingWriterAndSeesItsWrites()
        {
            var m = new ReadOptimizedLock<int[]>(new int[64]);
            var writer = m.Lock();
            var attempted = new Notification();
            var acquired = new Notification();

            var reader = ConcurrencyTestThread.Start(() =>
            {
                attempted.Notify();
                using var guard = m.Read();
                acquired.Notify();
                for (var i = 0; i < guard.Value.Length; i++)
                    Assert.Equal(i * 3, guard.Value[i]);
            });

            attempted.Wait();
            for (var i = 0; i < writer.Value.Length; i++)
                writer.Value[i] = i * 3;

            Thread.Sleep(5);
            Assert.False(acquired.HasBeenNotified);
            writer.Dispose();
            reader.Join();
            Assert.True(acquired.HasBeenNotified);
        }

        [Fact]
        public void WritersAreMutuallyExclusive()
        {
            // Each writer does a non-atomic read-modify-write of a counter; any overlap would lose an increment.
            const int Threads = 8;
            const int PerThread = 2000;
            var m = new ReadOptimizedLock<long>(0);
            using var start = new Barrier(Threads);
            var threads = ConcurrencyTestThread.StartMany(Threads, _ =>
            {
                start.SignalAndWait();
                for (var i = 0; i < PerThread; i++)
                {
                    using var guard = m.Lock();
                    var v = guard.Value;
                    guard.Value = v + 1;
                }
            });
            ConcurrencyTestThread.JoinAll(threads);
            Assert.Equal(Threads * PerThread, m.IntoInner());
        }

        [Fact]
        public void ReadersNeverObserveATornWrite()
        {
            // Writers keep both halves of a pair equal, but update them one at a time; readers must never see them
            // differ, and every read must see a value at least as new as one it saw before (no stale reads after a
            // newer one). Like the Rust, the lock prefers writers, so back-to-back writers can starve readers; the
            // writers pause between writes, as egglog's rare writers do, and every reader reads at least once.
            const int Writers = 2;
            const int Readers = 6;
            const int WritesPerWriter = 3000;
            var m = new ReadOptimizedLock<long[]>(new long[2]);
            var writersDone = 0;
            var reads = 0L;
            using var start = new Barrier(Writers + Readers);

            var writerThreads = ConcurrencyTestThread.StartMany(Writers, _ =>
            {
                start.SignalAndWait();
                for (var i = 0; i < WritesPerWriter; i++)
                {
                    using (var guard = m.Lock())
                    {
                        var pair = guard.Value;
                        pair[0]++;
                        Thread.SpinWait(20);
                        pair[1]++;
                    }

                    Thread.SpinWait(200);
                }

                Interlocked.Increment(ref writersDone);
            });

            var readerThreads = ConcurrencyTestThread.StartMany(Readers, _ =>
            {
                start.SignalAndWait();
                var last = 0L;
                do
                {
                    using var guard = m.Read();
                    var a = guard.Value[0];
                    var b = guard.Value[1];
                    Assert.Equal(a, b);
                    Assert.True(a >= last);
                    last = a;
                    Interlocked.Increment(ref reads);
                }
                while (Volatile.Read(ref writersDone) < Writers);
            });

            ConcurrencyTestThread.JoinAll(writerThreads);
            ConcurrencyTestThread.JoinAll(readerThreads);

            Assert.True(Interlocked.Read(ref reads) >= Readers);
            using var final = m.Read();
            Assert.Equal(Writers * WritesPerWriter, final.Value[0]);
            Assert.Equal(Writers * WritesPerWriter, final.Value[1]);
        }

        [Fact]
        public void WriterBlocksOnLongReadersThenProceeds()
        {
            // Readers hold the lock for a while from several threads; the writer must wait for all of them, then run.
            const int Readers = 4;
            var m = new ReadOptimizedLock<int>(0);
            using var holding = new CountdownEvent(Readers);
            var release = new Notification();
            var readerThreads = ConcurrencyTestThread.StartMany(Readers, _ =>
            {
                using var guard = m.Read();
                holding.Signal();
                release.Wait();
                Assert.Equal(0, guard.Value);
            });

            holding.Wait(TestContext.Current.CancellationToken);
            var writerDone = new Notification();
            var writer = ConcurrencyTestThread.Start(() =>
            {
                using var guard = m.Lock();
                guard.Value = 42;
                writerDone.Notify();
            });

            Assert.False(writerDone.WaitWithTimeout(TimeSpan.FromMilliseconds(50)));
            release.Notify();
            ConcurrencyTestThread.JoinAll(readerThreads);
            writer.Join();
            Assert.True(writerDone.HasBeenNotified);
            Assert.Equal(42, m.IntoInner());
        }

        [Fact]
        public void WriterIsAlwaysWokenWhenReadersDrain()
        {
            // Readers churn through short critical sections of random length, so the writer's drain often gives up
            // spinning and blocks; a lost wakeup would hang it, which the timeout turns into a failure.
            const int Readers = 4;
            const int Writes = 3000;
            var m = new ReadOptimizedLock<int>(0);
            var stop = 0;
            var readerThreads = ConcurrencyTestThread.StartMany(Readers, r =>
            {
                var random = new Random(r);
                while (Volatile.Read(ref stop) == 0)
                {
                    using (m.Read())
                        Thread.SpinWait(random.Next(0, 400));

                    if (random.Next(16) == 0)
                        Thread.Yield();
                }
            });

            var done = new Notification();
            var writer = ConcurrencyTestThread.Start(() =>
            {
                for (var i = 0; i < Writes; i++)
                {
                    using var guard = m.Lock();
                    guard.Value++;
                }

                done.Notify();
            });

            var finished = done.WaitWithTimeout(TimeSpan.FromSeconds(60));
            Volatile.Write(ref stop, 1);
            Assert.True(finished, "the writer was never woken");
            writer.Join();
            ConcurrencyTestThread.JoinAll(readerThreads);
            Assert.Equal(Writes, m.IntoInner());
        }

        [Fact]
        public void StripeCountIsAPowerOfTwoWithinBounds()
        {
            var m = new ReadOptimizedLock<int>(0);
            Assert.InRange(m.StripeCount, 1, ReadOptimizedLock<int>.MaxStripes);
            Assert.Equal(0, m.StripeCount & (m.StripeCount - 1));
        }

        [Fact]
        public void MixedReadersAndWritersMakeProgressWithoutPauses()
        {
            // Several writers and several readers hammer the lock with no pauses. Readers may be starved for a while
            // by back-to-back writers (as in the Rust), but nobody may deadlock, writers stay exclusive and no reader
            // may run concurrently with a writer.
            const int Writers = 4;
            const int Readers = 6;
            const int WritesPerWriter = 5000;
            var m = new ReadOptimizedLock<long[]>(new long[2]);
            var active = 0;
            var writersDone = 0;
            var violations = 0;
            var threads = new ConcurrencyTestThread[Writers + Readers];
            for (var w = 0; w < Writers; w++)
            {
                threads[w] = ConcurrencyTestThread.Start(() =>
                {
                    for (var i = 0; i < WritesPerWriter; i++)
                    {
                        using var guard = m.Lock();
                        if (Interlocked.Exchange(ref active, -1) != 0)
                            Interlocked.Increment(ref violations);

                        guard.Value[0]++;
                        guard.Value[1]++;
                        Volatile.Write(ref active, 0);
                    }

                    Interlocked.Increment(ref writersDone);
                });
            }

            for (var r = 0; r < Readers; r++)
            {
                threads[Writers + r] = ConcurrencyTestThread.Start(() =>
                {
                    while (Volatile.Read(ref writersDone) < Writers)
                    {
                        using var guard = m.Read();
                        if (Volatile.Read(ref active) < 0)
                            Interlocked.Increment(ref violations);

                        if (guard.Value[0] != guard.Value[1])
                            Interlocked.Increment(ref violations);
                    }
                });
            }

            var deadline = DateTime.UtcNow.AddSeconds(60);
            while (Volatile.Read(ref writersDone) < Writers && DateTime.UtcNow < deadline)
                Thread.Sleep(10);

            Assert.True(Volatile.Read(ref writersDone) == Writers, "writers did not finish within 60 s");
            ConcurrencyTestThread.JoinAll(threads);
            Assert.Equal(0, violations);
            Assert.Equal(Writers * WritesPerWriter, m.IntoInner()[0]);
        }

    }

}

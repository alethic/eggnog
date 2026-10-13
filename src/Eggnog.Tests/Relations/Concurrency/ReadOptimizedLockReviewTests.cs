using System;
using System.Threading;

using Xunit;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// Review probes for <see cref="ReadOptimizedLock{T}"/>: liveness and exclusion under mixed contention.
    /// </summary>
    public sealed class ReadOptimizedLockReviewTests
    {

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

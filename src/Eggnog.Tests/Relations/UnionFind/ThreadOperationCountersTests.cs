using System;
using System.Runtime.CompilerServices;
using System.Threading;

using Xunit;

namespace Eggnog.Relations.Tests
{

    public class ThreadOperationCountersTests
    {

        /// <summary>
        /// Starts a thread whose counter is held odd, as if it were inside a union-find operation, from the moment it
        /// sets <paramref name="entered"/> until <paramref name="release"/> is set. The counter is always restored, so
        /// a failing test cannot leave every later reset waiting.
        /// </summary>
        /// <param name="entered">Set once the thread is "inside".</param>
        /// <param name="release">Set to let the thread leave.</param>
        /// <returns>The started thread.</returns>
        internal static Thread HoldInsideOperation(ManualResetEventSlim entered, ManualResetEventSlim release)
        {
            var thread = new Thread(() =>
            {
                ref var counter = ref ThreadOperationCounters.Current();
                var ticket = counter + 1;
                Volatile.Write(ref counter, ticket);
                try
                {
                    entered.Set();
                    release.Wait();
                }
                finally
                {
                    Volatile.Write(ref counter, ticket + 1);
                }
            });
            thread.IsBackground = true;
            thread.Start();
            return thread;
        }

        [Fact]
        public void OperationsLeaveTheCounterEvenAndAdvanceItByTwo()
        {
            var uf = new ConcurrentUnionFind();
            ref var counter = ref ThreadOperationCounters.Current();
            var before = Volatile.Read(ref counter);
            Assert.Equal(0u, before & 1);

            uf.Find(3);
            uf.Union(3, 4);
            uf.SameSet(3, 4);
            uf.DeepCopy();
            Assert.Equal(before + 8, Volatile.Read(ref counter));

            // Rejected identifiers fail before the bracket.
            Assert.Throws<ArgumentOutOfRangeException>(() => uf.Find(uint.MaxValue));
            Assert.Equal(before + 8, Volatile.Read(ref counter));
        }

        [Fact]
        public void EachThreadHasItsOwnCounter()
        {
            ref var mine = ref ThreadOperationCounters.Current();
            var start = Volatile.Read(ref mine);
            var other = new Thread(() =>
            {
                ref var theirs = ref ThreadOperationCounters.Current();
                for (var i = 0; i < 1000; i++)
                    theirs++;
            });
            other.Start();
            other.Join();

            Assert.Equal(start, Volatile.Read(ref mine));
            Assert.True(Unsafe.AreSame(ref mine, ref ThreadOperationCounters.Current()));
        }

        [Fact]
        public void WaitForOperationsWaitsForAThreadInsideAnOperation()
        {
            using var entered = new ManualResetEventSlim(false);
            using var release = new ManualResetEventSlim(false);
            using var done = new ManualResetEventSlim(false);
            var holder = HoldInsideOperation(entered, release);
            try
            {
                Assert.True(entered.Wait(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
                var waiter = new Thread(() =>
                {
                    ThreadOperationCounters.WaitForOperations();
                    done.Set();
                });
                waiter.IsBackground = true;
                waiter.Start();

                Assert.False(done.Wait(TimeSpan.FromMilliseconds(200), TestContext.Current.CancellationToken));
                release.Set();
                Assert.True(done.Wait(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
            }
            finally
            {
                release.Set();
                holder.Join();
            }
        }

        [Fact]
        public void WaitForOperationsIsNotStarvedByAThreadThatKeepsOperating()
        {
            var busy = new ConcurrentUnionFind(1024);
            var stop = 0;
            var worker = new Thread(() =>
            {
                uint i = 0;
                while (Volatile.Read(ref stop) == 0)
                {
                    busy.Union(i & 1023, (i * 7) & 1023);
                    busy.Find(i & 1023);
                    i++;
                }
            });
            worker.IsBackground = true;
            worker.Start();
            try
            {
                for (var round = 0; round < 20; round++)
                {
                    var waiter = new Thread(ThreadOperationCounters.WaitForOperations);
                    waiter.IsBackground = true;
                    waiter.Start();
                    Assert.True(waiter.Join(TimeSpan.FromSeconds(10)));
                }
            }
            finally
            {
                Volatile.Write(ref stop, 1);
                worker.Join();
            }
        }

    }

}

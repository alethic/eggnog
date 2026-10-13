using System;
using System.Threading;
using System.Threading.Tasks;

using Xunit;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// <see cref="ParallelismBudget"/> and <see cref="ParallelismLease"/> (colored-contexts spec §10.9, §17 M10.3): leases
    /// take threads and give them back, <see cref="ParallelismBudget.Lease"/> blocks until enough are free,
    /// <see cref="ParallelismBudget.TryLease"/> never blocks, impossible requests and over-releases are refused, and
    /// concurrent leases never hold more than the total.
    /// </summary>
    public class ParallelismBudgetTests
    {

        [Fact]
        public void ANewBudgetHasEveryThreadAvailable()
        {
            var budget = new ParallelismBudget(6);
            Assert.Equal(6, budget.Total);
            Assert.Equal(6, budget.Available);
            Assert.Throws<ArgumentOutOfRangeException>(() => new ParallelismBudget(0));
            Assert.Throws<ArgumentOutOfRangeException>(() => new ParallelismBudget(-3));
        }

        [Fact]
        public void ALeaseTakesThreadsAndItsDisposalReturnsThem()
        {
            var budget = new ParallelismBudget(4);
            using (var lease = budget.Lease(3))
            {
                Assert.Same(budget, lease.Budget);
                Assert.Equal(3, lease.Threads);
                Assert.Equal(1, budget.Available);
                using (var second = budget.Lease(1))
                    Assert.Equal(0, budget.Available);

                Assert.Equal(1, budget.Available);
            }

            Assert.Equal(4, budget.Available);
        }

        [Fact]
        public void TryLeaseSucceedsOnlyWhenEnoughThreadsAreFree()
        {
            var budget = new ParallelismBudget(3);
            Assert.True(budget.TryLease(2, out var first));
            Assert.Equal(2, first.Threads);
            Assert.False(budget.TryLease(2, out var failed));
            Assert.Null(failed.Budget);
            Assert.Equal(0, failed.Threads);
            failed.Dispose();
            Assert.Equal(1, budget.Available);

            Assert.True(budget.TryLease(1, out var second));
            Assert.Equal(0, budget.Available);
            first.Dispose();
            second.Dispose();
            Assert.Equal(3, budget.Available);
        }

        [Fact]
        public void TheDefaultLeaseHoldsNothing()
        {
            var lease = default(ParallelismLease);
            Assert.Null(lease.Budget);
            Assert.Equal(0, lease.Threads);
            lease.Dispose();
        }

        [Fact]
        public void RequestsThatCanNeverBeGrantedAreRefused()
        {
            var budget = new ParallelismBudget(2);
            Assert.Throws<ArgumentOutOfRangeException>(() => budget.Lease(0));
            Assert.Throws<ArgumentOutOfRangeException>(() => budget.Lease(3));
            Assert.Throws<ArgumentOutOfRangeException>(() => budget.TryLease(0, out _));
            Assert.Throws<ArgumentOutOfRangeException>(() => budget.TryLease(3, out _));
            Assert.Throws<ArgumentOutOfRangeException>(() => budget.Release(0));
            Assert.Equal(2, budget.Available);
        }

        [Fact]
        public void ReleasingMoreThanWasLeasedIsRefused()
        {
            var budget = new ParallelismBudget(2);
            Assert.Throws<InvalidOperationException>(() => budget.Release(1));

            var lease = budget.Lease(2);
            lease.Dispose();
            Assert.Equal(2, budget.Available);
            Assert.Throws<InvalidOperationException>(() => lease.Dispose());
            Assert.Equal(2, budget.Available);

            var partial = budget.Lease(1);
            Assert.Throws<InvalidOperationException>(() => budget.Release(2));
            partial.Dispose();
            Assert.Equal(2, budget.Available);
        }

        /// <summary>
        /// A lease waits while the budget is short and is granted once enough threads come back.
        /// </summary>
        [Fact]
        public void LeaseBlocksUntilEnoughThreadsAreReleased()
        {
            var budget = new ParallelismBudget(3);
            var held = budget.Lease(2);
            var started = new ManualResetEventSlim();
            var waiter = Task.Factory.StartNew(() =>
            {
                started.Set();
                using var lease = budget.Lease(2);
                return budget.Available;
            }, TaskCreationOptions.LongRunning);

            Assert.True(started.Wait(TimeSpan.FromSeconds(30)));
            Assert.False(waiter.Wait(TimeSpan.FromMilliseconds(200)));
            Assert.Equal(1, budget.Available);

            held.Dispose();
            Assert.True(waiter.Wait(TimeSpan.FromSeconds(30)));
            Assert.Equal(1, waiter.Result);
            Assert.Equal(3, budget.Available);
        }

        /// <summary>
        /// Many threads leasing and releasing at once never hold more than the total, and every thread comes back.
        /// </summary>
        [Fact]
        public void ConcurrentLeasesNeverHoldMoreThanTheTotal()
        {
            const int Total = 5;
            var budget = new ParallelismBudget(Total);
            var inUse = 0;
            var peak = 0;
            var threads = ConcurrencyTestThread.StartMany(12, t =>
            {
                var random = new Random(31 + t);
                for (var round = 0; round < 200; round++)
                {
                    var want = random.Next(1, Total + 1);
                    ParallelismLease lease;
                    if (round % 3 == 0)
                    {
                        if (budget.TryLease(want, out lease) == false)
                            continue;
                    }
                    else
                    {
                        lease = budget.Lease(want);
                    }

                    using (lease)
                    {
                        var now = Interlocked.Add(ref inUse, want);
                        Assert.InRange(now, want, Total);
                        Assert.InRange(budget.Available, 0, Total - want);
                        InterlockedMax(ref peak, now);
                        Thread.SpinWait(random.Next(50));
                        Interlocked.Add(ref inUse, -want);
                    }
                }
            });

            ConcurrencyTestThread.JoinAll(threads);
            Assert.InRange(peak, 1, Total);
            Assert.Equal(Total, budget.Available);
            Assert.Equal(0, inUse);
        }

        static void InterlockedMax(ref int target, int value)
        {
            var current = Volatile.Read(ref target);
            while (value > current)
            {
                var seen = Interlocked.CompareExchange(ref target, value, current);
                if (seen == current)
                    return;

                current = seen;
            }
        }

    }

}

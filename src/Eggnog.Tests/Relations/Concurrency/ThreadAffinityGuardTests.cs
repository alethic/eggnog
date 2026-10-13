using System;
using System.Threading;

using Xunit;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// Tests for <see cref="ThreadAffinityGuard"/>, the thread domain of colored contexts (colored-contexts spec §8.3,
    /// X15): re-entrant on its owner, refusing (never waiting for) a second thread.
    /// </summary>
    public class ThreadAffinityGuardTests
    {

        [Fact]
        public void EnterIsReentrantOnTheOwningThread()
        {
            var guard = new ThreadAffinityGuard();
            Assert.False(guard.IsHeld);
            Assert.Equal(0, guard.Depth);

            using (guard.Enter())
            {
                Assert.True(guard.IsHeld);
                Assert.Equal(1, guard.Depth);
                using (guard.Enter())
                    Assert.Equal(2, guard.Depth);

                Assert.Equal(1, guard.Depth);
                Assert.True(guard.IsHeld);
            }

            Assert.False(guard.IsHeld);
            Assert.Equal(0, guard.Depth);
        }

        [Fact]
        public void ASecondThreadIsRefusedWhileTheGuardIsHeldAndAdmittedAfterwards()
        {
            var guard = new ThreadAffinityGuard();
            using var held = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            var owner = ConcurrencyTestThread.Start(() =>
            {
                using (guard.Enter())
                {
                    held.Set();
                    release.Wait();
                }
            });

            held.Wait();
            try
            {
                // Another thread sees depth zero and cannot enter.
                Assert.True(guard.IsHeld);
                Assert.Equal(0, guard.Depth);
                var e = Assert.Throws<InvalidOperationException>(() => guard.Enter());
                Assert.Contains("two threads", e.Message);
                Assert.Throws<InvalidOperationException>(() => guard.Exit());
            }
            finally
            {
                release.Set();
                owner.Join();
            }

            using (guard.Enter())
                Assert.Equal(1, guard.Depth);
        }

        [Fact]
        public void ExitWithoutHoldingThrowsAndDefaultScopesDoNothing()
        {
            var guard = new ThreadAffinityGuard();
            Assert.Throws<InvalidOperationException>(() => guard.Exit());

            var scope = default(ThreadAffinityScope);
            scope.Dispose();
            Assert.False(guard.IsHeld);
        }

        [Fact]
        public void TheGuardAdmitsOneThreadAtATime()
        {
            var guard = new ThreadAffinityGuard();
            var inside = 0;
            var admitted = 0;
            var refused = 0;
            using var start = new ManualResetEventSlim();
            var threads = ConcurrencyTestThread.StartMany(8, _ =>
            {
                start.Wait();
                for (var i = 0; i < 20_000; i++)
                {
                    ThreadAffinityScope scope;
                    try
                    {
                        scope = guard.Enter();
                    }
                    catch (InvalidOperationException)
                    {
                        Interlocked.Increment(ref refused);
                        continue;
                    }

                    using (scope)
                    {
                        Assert.Equal(1, Interlocked.Increment(ref inside));
                        using (guard.Enter())
                            Assert.Equal(2, guard.Depth);

                        Interlocked.Increment(ref admitted);
                        Assert.Equal(0, Interlocked.Decrement(ref inside));
                    }
                }
            });

            start.Set();
            ConcurrencyTestThread.JoinAll(threads);
            Assert.Equal(8 * 20_000, admitted + refused);
            Assert.True(admitted > 0);
            Assert.False(guard.IsHeld);
        }

    }

}

using System;
using System.Collections.Concurrent;
using System.Threading;

using Xunit;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// The help-while-waiting scope that stands in for rayon's <c>in_place_scope</c>.
    /// </summary>
    [Collection(ExecutorStressCollection.Name)]
    public class WorkScopeTests
    {

        [Theory]
        [InlineData(1)]
        [InlineData(2)]
        [InlineData(8)]
        public void RunsEveryItemIncludingItemsSpawnedByItems(int dop)
        {
            var scope = new WorkScope(dop);
            var count = 0;
            for (var i = 0; i < 50; i++)
                scope.Spawn(new TestWork(() =>
                {
                    Interlocked.Increment(ref count);
                    for (var j = 0; j < 10; j++)
                        scope.Spawn(new TestWork(() => Interlocked.Increment(ref count)));
                }));

            scope.Wait();
            Assert.Equal(550, count);
            Assert.Equal(0, scope.Pending);
        }

        [Fact]
        public void OneThreadRunsEverythingOnTheWaiter()
        {
            var scope = new WorkScope(1);
            var waiter = Environment.CurrentManagedThreadId;
            var threads = new ConcurrentBag<int>();
            for (var i = 0; i < 20; i++)
                scope.Spawn(new TestWork(() => threads.Add(Environment.CurrentManagedThreadId)));

            scope.Wait();
            Assert.Equal(20, threads.Count);
            Assert.All(threads, t => Assert.Equal(waiter, t));
        }

        [Fact]
        public void ConcurrencyStaysWithinTheDegree()
        {
            var scope = new WorkScope(3);
            var active = 0;
            var peak = 0;
            for (var i = 0; i < 60; i++)
                scope.Spawn(new TestWork(() =>
                {
                    var now = Interlocked.Increment(ref active);
                    int seen;
                    while ((seen = Volatile.Read(ref peak)) < now && Interlocked.CompareExchange(ref peak, now, seen) != seen)
                    {
                    }

                    Thread.Sleep(1);
                    Interlocked.Decrement(ref active);
                }));

            scope.Wait();
            Assert.InRange(peak, 1, 3);
        }

        [Fact]
        public void NestedScopesComplete()
        {
            // Every outer item waits on a scope of its own, as a rule task waits on each block of a decomposed plan.
            var outer = new WorkScope(2);
            var count = 0;
            for (var i = 0; i < 16; i++)
                outer.Spawn(new TestWork(() =>
                {
                    var inner = new WorkScope(2);
                    for (var j = 0; j < 16; j++)
                        inner.Spawn(new TestWork(() => Interlocked.Increment(ref count)));

                    inner.Wait();
                }));

            outer.Wait();
            Assert.Equal(256, count);
        }

        [Fact]
        public void TheFirstExceptionIsRethrownAfterEveryItemFinished()
        {
            var scope = new WorkScope(4);
            var finished = 0;
            for (var i = 0; i < 30; i++)
            {
                var n = i;
                scope.Spawn(new TestWork(() =>
                {
                    if (n % 10 == 3)
                        throw new InvalidOperationException("item " + n);

                    Interlocked.Increment(ref finished);
                }));
            }

            var e = Assert.Throws<InvalidOperationException>(scope.Wait);
            Assert.StartsWith("item ", e.Message);
            Assert.Equal(27, finished);
        }

    }

}

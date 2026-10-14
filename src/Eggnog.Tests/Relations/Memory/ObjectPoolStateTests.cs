using System;
using System.Threading;
using System.Threading.Tasks;

using Xunit;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// Tests for <see cref="ObjectPoolState{T}"/>, one thread's share of an object pool, and its spin guard.
    /// </summary>
    public sealed class ObjectPoolStateTests
    {

        /// <summary>
        /// The guard excludes: a thread that enters a state another thread holds, as a pool clear does, waits until that
        /// thread leaves, then holds the state itself. Repeated, each time with the state already holding the objects
        /// the earlier rounds pushed.
        /// </summary>
        [Fact]
        public async Task EnteringAHeldStateWaitsUntilItIsLeft()
        {
            const int Rounds = 5;
            var state = new ObjectPoolState<object>();
            var items = new object[Rounds];
            for (var round = 0; round < Rounds; round++)
            {
                var item = items[round] = new object();
                using var starting = new ManualResetEventSlim();
                var inside = 0;

                state.Enter();
                var other = Task.Factory.StartNew(() =>
                {
                    starting.Set();
                    state.Enter();
                    Volatile.Write(ref inside, 1);
                    var pushed = state.Push(item, 8, 64);
                    state.Exit();
                    return pushed;
                }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);

                Assert.True(starting.Wait(TimeSpan.FromSeconds(30)));
                Assert.NotSame(other, await Task.WhenAny(other, Task.Delay(TimeSpan.FromMilliseconds(100))));
                Assert.Equal(0, Volatile.Read(ref inside));
                Assert.Equal(round, state.Count);
                state.Exit();

                Assert.True(await other.WaitAsync(TimeSpan.FromSeconds(30)));
            }

            state.Enter();
            Assert.Equal(Rounds, state.Count);
            Assert.Equal(8 * Rounds, state.Bytes);
            for (var round = Rounds - 1; round >= 0; round--)
                Assert.Same(items[round], state.Pop());

            Assert.Null(state.Pop());
            state.Exit();
        }

    }

}

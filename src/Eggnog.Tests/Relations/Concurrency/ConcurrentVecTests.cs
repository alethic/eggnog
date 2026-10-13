using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

using Xunit;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// Tests for <see cref="ConcurrentVec{T}"/>: ports of egglog's concurrency/src/tests.rs:226-356, plus randomized
    /// comparisons against <see cref="List{T}"/> and concurrent reader stress.
    /// </summary>
    public sealed class ConcurrentVecTests
    {

        [Fact]
        public void BasicParallelPush()
        {
            const int Threads = 10;
            const int PerThread = 10;
            var v = new ConcurrentVec<int>(0);
            var got = new List<int>[Threads];
            var threads = ConcurrencyTestThread.StartMany(Threads, i =>
            {
                var mine = new List<int>();
                for (var j = 0; j < PerThread; j++)
                    mine.Add(v.Push(i * PerThread + j));

                got[i] = mine;
            });
            ConcurrencyTestThread.JoinAll(threads);

            var results = got.SelectMany(x => x).OrderBy(x => x).ToArray();
            Assert.Equal(Threads * PerThread, results.Length);
            Assert.Equal(Enumerable.Range(0, Threads * PerThread), results);

            var slice = v.Read();
            Assert.Equal(Threads * PerThread, slice.Length);
            var sorted = slice.ToArray();
            Array.Sort(sorted);
            Assert.Equal(Enumerable.Range(0, Threads * PerThread), sorted);
        }

        [Fact]
        public void ExposesExactlyThePushedElements()
        {
            // egglog's concurrent_vec_drops_initialized_values checks that exactly the pushed elements are dropped. The
            // collector owns element lifetimes here, so check instead that exactly the pushed elements are published.
            var v = new ConcurrentVec<object>(0);
            var a = new object();
            var b = new object();
            Assert.Equal(0, v.Push(a));
            Assert.Equal(1, v.Push(b));
            var slice = v.Read();
            Assert.Equal(2, slice.Length);
            Assert.Same(a, slice[0]);
            Assert.Same(b, slice[1]);
            Assert.Equal(2, v.Count);
            Assert.Throws<ArgumentOutOfRangeException>(() => v[2]);
        }

        [Fact]
        public void ResizeWithSerialInit()
        {
            var v = new ConcurrentVec<int>(0);
            v.Push(10);
            v.Push(20);
            v.ResizeWith(6, () => 99);

            var slice = v.Read();
            Assert.Equal(6, slice.Length);
            Assert.Equal(10, slice[0]);
            Assert.Equal(20, slice[1]);
            foreach (var value in slice[2..])
                Assert.Equal(99, value);
        }

        [Fact]
        public void ResizeWithParallelInit()
        {
            var v = new ConcurrentVec<int>(0);
            using var barrier = new Barrier(3);

            var left = ConcurrencyTestThread.Start(() =>
            {
                barrier.SignalAndWait();
                v.ResizeWith(64, () => 1);
            });

            var right = ConcurrencyTestThread.Start(() =>
            {
                barrier.SignalAndWait();
                v.ResizeWith(128, () => 2);
            });

            barrier.SignalAndWait(TestContext.Current.CancellationToken);
            left.Join();
            right.Join();

            var slice = v.Read();
            Assert.Equal(128, slice.Length);
            foreach (var value in slice)
                Assert.True(value == 1 || value == 2);

            for (var i = 1; i < 64; i++)
                Assert.Equal(slice[i - 1], slice[i]);

            for (var i = 65; i < 128; i++)
                Assert.Equal(slice[i - 1], slice[i]);
        }

        [Fact]
        public void ResizeWithNoopWhenSized()
        {
            var v = new ConcurrentVec<int>(0);
            v.ResizeWith(4, () => 7);

            var counter = 0;
            var threads = ConcurrencyTestThread.StartMany(4, _ => v.ResizeWith(4, () =>
            {
                Interlocked.Increment(ref counter);
                return 9;
            }));
            ConcurrencyTestThread.JoinAll(threads);

            v.ResizeWith(3, () =>
            {
                Interlocked.Increment(ref counter);
                return 9;
            });

            Assert.Equal(0, Volatile.Read(ref counter));
            Assert.Equal(new[] { 7, 7, 7, 7 }, v.Read().ToArray());
        }

        [Fact]
        public void ResizeWithAfterPushInitializesEveryNewSlot()
        {
            // egglog's resize_with only writes slot new_len - 1 when push already grew the storage
            // (concurrent_vec.rs:58-73, 94-114), publishing uninitialized slots. Every new slot must hold the factory's
            // value, and the factory must run once per new slot.
            var v = new ConcurrentVec<int>(0);
            for (var i = 0; i < 5; i++)
                v.Push(i);

            var calls = 0;
            v.ResizeWith(7, () =>
            {
                calls++;
                return 99;
            });

            Assert.Equal(2, calls);
            Assert.Equal(new[] { 0, 1, 2, 3, 4, 99, 99 }, v.Read().ToArray());
        }

        [Theory]
        [InlineData(1)]
        [InlineData(2)]
        [InlineData(3)]
        [InlineData(4)]
        public void MatchesReferenceOnRandomOperations(int seed)
        {
            var random = new Random(seed);
            var v = new ConcurrentVec<long>(random.Next(0, 40));
            var reference = new List<long>();
            for (var step = 0; step < 3000; step++)
            {
                switch (random.Next(4))
                {
                    case 0:
                    case 1:
                        var item = random.NextInt64();
                        Assert.Equal(reference.Count, v.Push(item));
                        reference.Add(item);
                        break;
                    case 2:
                        var newLength = reference.Count + random.Next(-5, 20);
                        var fill = random.NextInt64();
                        v.ResizeWith(newLength, () => fill);
                        while (reference.Count < newLength)
                            reference.Add(fill);
                        break;
                    default:
                        if (reference.Count > 0)
                        {
                            var index = random.Next(reference.Count);
                            Assert.Equal(reference[index], v[index]);
                        }

                        break;
                }

                Assert.Equal(reference.Count, v.Count);
            }

            Assert.Equal(reference, v.Read().ToArray());
        }

        [Fact]
        public void ReadersSeeConsistentPrefixesDuringPushes()
        {
            // One pusher appends i at index i, starting from the smallest capacity so the storage is copied many
            // times; readers keep checking that every snapshot is a prefix of 0, 1, 2, ... and that lengths never
            // shrink.
            const int Items = 200_000;
            var v = new ConcurrentVec<int>(0);
            var done = 0;
            var pusher = ConcurrencyTestThread.Start(() =>
            {
                for (var i = 0; i < Items; i++)
                    Assert.Equal(i, v.Push(i));

                Volatile.Write(ref done, 1);
            });

            var readers = ConcurrencyTestThread.StartMany(3, r =>
            {
                var random = new Random(r);
                var last = 0;
                while (Volatile.Read(ref done) == 0)
                {
                    var slice = v.Read();
                    Assert.True(slice.Length >= last);
                    last = slice.Length;
                    if (slice.Length == 0)
                        continue;

                    for (var k = 0; k < 16; k++)
                    {
                        var index = random.Next(slice.Length);
                        Assert.Equal(index, slice[index]);
                        Assert.Equal(index, v[index]);
                    }

                    Assert.Equal(slice.Length - 1, slice[^1]);
                }
            });

            pusher.Join();
            ConcurrencyTestThread.JoinAll(readers);
            Assert.Equal(Enumerable.Range(0, Items), v.Read().ToArray());
        }

        [Fact]
        public void ConcurrentPushersAndResizersAgree()
        {
            // Pushers and resizers race; afterwards every pushed value appears exactly once, at the index its push
            // returned, and every other slot holds the resize fill.
            const int Threads = 6;
            const int Ops = 2000;
            var v = new ConcurrentVec<long>(1);
            var pushed = new List<(int Index, long Value)>[Threads];
            using var start = new Barrier(Threads);
            var threads = ConcurrencyTestThread.StartMany(Threads, t =>
            {
                var random = new Random(t);
                var mine = new List<(int, long)>();
                start.SignalAndWait();
                for (var i = 0; i < Ops; i++)
                {
                    if (random.Next(4) == 0)
                    {
                        v.ResizeWith(v.Count + random.Next(1, 8), () => -1);
                    }
                    else
                    {
                        var value = (long)t << 32 | (uint)i;
                        mine.Add((v.Push(value), value));
                    }
                }

                pushed[t] = mine;
            });
            ConcurrencyTestThread.JoinAll(threads);

            var slice = v.Read();
            var owned = new bool[slice.Length];
            foreach (var (index, value) in pushed.SelectMany(x => x))
            {
                Assert.Equal(value, slice[index]);
                Assert.False(owned[index]);
                owned[index] = true;
            }

            for (var i = 0; i < slice.Length; i++)
                if (owned[i] == false)
                    Assert.Equal(-1, slice[i]);
        }

        [Fact]
        public void RejectsBadArguments()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new ConcurrentVec<int>(-1));
            var v = new ConcurrentVec<int>();
            Assert.Throws<ArgumentOutOfRangeException>(() => v[0]);
            Assert.Throws<ArgumentOutOfRangeException>(() => v[-1]);
            Assert.Throws<ArgumentNullException>(() => v.ResizeWith(3, null!));
            v.ResizeWith(0, null!);
        }

        [Fact]
        public void ReentrantAppendFromResizeFactoryThrowsAndLeavesTheVectorUsable()
        {
            // The Rust's write lock is a non-reentrant Mutex, so an append from inside resize_with's closure deadlocks;
            // here it is detected and rejected, and the failed resize publishes nothing.
            var v = new ConcurrentVec<int>(0);
            Assert.Throws<InvalidOperationException>(() => v.ResizeWith(3, () =>
            {
                v.Push(42);
                return 7;
            }));
            Assert.Throws<InvalidOperationException>(() => v.ResizeWith(3, () =>
            {
                v.ResizeWith(10, () => 1);
                return 7;
            }));
            Assert.Equal(0, v.Count);

            Assert.Equal(0, v.Push(5));
            v.ResizeWith(3, () => 9);
            Assert.Equal(new[] { 5, 9, 9 }, v.Read().ToArray());
        }

        [Fact]
        public void ThrowingFactoryPublishesNothing()
        {
            var v = new ConcurrentVec<int>(0);
            v.Push(1);
            var calls = 0;
            Assert.Throws<FormatException>(() => v.ResizeWith(5, () => ++calls == 3 ? throw new FormatException() : calls));
            Assert.Equal(1, v.Count);

            // The partly filled slots are refilled by the next resize.
            v.ResizeWith(3, () => 8);
            Assert.Equal(new[] { 1, 8, 8 }, v.Read().ToArray());
            Assert.Equal(3, v.Push(4));
        }

        [Fact]
        public void ReentrantPushFromResizeFactoryIsNotSilentlyLost()
        {
            // egglog guards ConcurrentVec writes with a std::sync::Mutex (concurrent_vec.rs:27,63), which is not
            // reentrant: a push from inside resize_with's closure deadlocks or panics. C#'s Monitor is reentrant, so a
            // nested Push that ran would publish index 0 and the outer loop would then overwrite slot 0 with the
            // factory's value, leaving Push's index pointing at another element. ConcurrentVec rejects the reentrant
            // call with an InvalidOperationException, the convention for misuse; a nested push that is accepted must
            // keep the pushed element.
            var v = new ConcurrentVec<int>(0);
            var pushedIndex = -1;
            Exception? failure = null;
            try
            {
                v.ResizeWith(3, () =>
                {
                    if (pushedIndex < 0)
                        pushedIndex = v.Push(42);

                    return 7;
                });
            }
            catch (InvalidOperationException e)
            {
                failure = e;
            }

            if (failure is null)
                Assert.Equal(42, v[pushedIndex]);
        }

    }

}

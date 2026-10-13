using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;

using Xunit;

namespace Eggnog.Relations.Tests
{

    public class ConcurrentUnionFindTests
    {

        static uint[] Range(int start, int count) => Enumerable.Range(start, count).Select(i => (uint)i).ToArray();

        /// <summary>
        /// Starts <paramref name="count"/> threads that run <paramref name="body"/> with their index, releases them
        /// together, joins them, and rethrows the first failure.
        /// </summary>
        static void RunThreads(int count, Action<int> body)
        {
            var errors = new ConcurrentQueue<Exception>();
            using var start = new ManualResetEventSlim(false);
            var threads = new Thread[count];
            for (var t = 0; t < count; t++)
            {
                var index = t;
                threads[t] = new Thread(() =>
                {
                    start.Wait();
                    try
                    {
                        body(index);
                    }
                    catch (Exception e)
                    {
                        errors.Enqueue(e);
                    }
                });
                threads[t].Start();
            }

            start.Set();
            foreach (var thread in threads)
                thread.Join();

            if (errors.TryDequeue(out var error))
                throw new AggregateException(error);
        }

        /// <summary>
        /// Port of <c>concurrent/tests.rs</c> <c>uf_single_theaded</c>.
        /// </summary>
        [Fact]
        public void SingleThreaded()
        {
            var uf = new ConcurrentUnionFind();
            var ids1 = Range(0, 100);
            var ids2 = Range(100, 100);

            foreach (var ids in new[] { ids1, ids2 })
            {
                for (var i = 0; i + 1 < ids.Length; i++)
                {
                    var (parent, child) = uf.Union(ids[i], ids[i + 1]);
                    Assert.Equal(parent, uf.Find(ids[i]));
                    Assert.Equal(parent, uf.Find(ids[i + 1]));
                    Assert.True(child == ids[i] || child == ids[i + 1]);
                }
            }

            for (var i = 0; i + 1 < ids1.Length; i++)
            {
                Assert.Equal(uf.Find(ids1[i]), uf.Find(ids1[i + 1]));
                Assert.True(uf.SameSet(ids1[i], ids1[i + 1]));
            }

            for (var i = 0; i + 1 < ids2.Length; i++)
                Assert.Equal(uf.Find(ids2[i]), uf.Find(ids2[i + 1]));
            Assert.NotEqual(uf.Find(ids1[0]), uf.Find(ids2[0]));

            uf.Union(ids1[5], ids2[20]);

            var target = uf.Find(ids1[0]);
            Assert.All(ids1.Concat(ids2), id => Assert.Equal(target, uf.Find(id)));
        }

        /// <summary>
        /// Port of <c>concurrent/tests.rs</c> <c>uf_multi_threaded</c>.
        /// </summary>
        [Fact]
        public void MultiThreaded()
        {
            var uf = new ConcurrentUnionFind();
            var ids1 = Range(0, 100);
            var ids2 = Range(100, 100);

            RunThreads(100, _ =>
            {
                foreach (var ids in new[] { ids1, ids2 })
                {
                    for (var i = 0; i + 1 < ids.Length; i++)
                    {
                        var (parent, child) = uf.Union(ids[i], ids[i + 1]);
                        Assert.True(uf.SameSet(parent, child));
                        Assert.True(uf.SameSet(ids[i], child));
                        Assert.True(uf.SameSet(ids[i], parent));
                        Assert.True(uf.SameSet(ids[i + 1], child));
                        Assert.True(uf.SameSet(ids[i + 1], parent));
                    }
                }
            });

            for (var i = 0; i + 1 < ids1.Length; i++)
            {
                Assert.Equal(uf.Find(ids1[i]), uf.Find(ids1[i + 1]));
                Assert.True(uf.SameSet(ids1[i], ids1[i + 1]));
            }

            for (var i = 0; i + 1 < ids2.Length; i++)
                Assert.Equal(uf.Find(ids2[i]), uf.Find(ids2[i + 1]));
            Assert.NotEqual(uf.Find(ids1[0]), uf.Find(ids2[0]));

            RunThreads(100, tid => uf.Union(ids1[tid], ids2[tid]));

            var target = uf.Find(ids1[0]);
            Assert.All(ids1.Concat(ids2), id => Assert.Equal(target, uf.Find(id)));
        }

        /// <summary>
        /// Port of <c>concurrent/tests.rs</c> <c>buffer_single_threaded</c>: access within capacity does not grow;
        /// access beyond it grows to the next power of two, and every new slot is its own root. (The Rust test also
        /// counts element drops, which has no .NET counterpart for <see cref="uint"/> slots.)
        /// </summary>
        [Fact]
        public void GrowthRoundsUpToAPowerOfTwo()
        {
            var uf = new ConcurrentUnionFind(4);
            Assert.Equal(32, uf.Capacity);
            Assert.Equal(31u, uf.Find(31));
            Assert.Equal(32, uf.Capacity);

            Assert.Equal(99u, uf.Find(99));
            Assert.Equal(128, uf.Capacity);
            for (uint i = 0; i < 128; i++)
                Assert.Equal(i, uf.Find(i));

            Assert.True(uf.SameSet(1000, 1000));
            Assert.Equal(1024, uf.Capacity);
            Assert.Equal((5u, 5000u), uf.Union(5000, 5));
            Assert.Equal(8192, uf.Capacity);

            Assert.Equal(0, new ConcurrentUnionFind(0).Capacity);
            Assert.Equal(32, new ConcurrentUnionFind().Capacity);
            Assert.Equal(1L << 20, new ConcurrentUnionFind((1 << 20) - 1).Capacity);
            Assert.Equal(1L << 21, new ConcurrentUnionFind((1 << 20) + 1).Capacity);
        }

        /// <summary>
        /// Port of <c>concurrent/tests.rs</c> <c>buffer_multi_threaded</c>: 100 threads demand ever larger
        /// capacities at once, and each sees every slot it asked for as a fresh root.
        /// </summary>
        [Fact]
        public void ConcurrentGrowth()
        {
            var uf = new ConcurrentUnionFind(1);
            RunThreads(100, x =>
            {
                var length = (uint)(x * 1000);
                if (length == 0)
                    return;

                Assert.Equal(length - 1, uf.Find(length - 1));
                Assert.True(uf.Capacity >= length);
                var wrong = 0;
                for (uint i = 0; i < length; i++)
                    if (uf.Find(i) != i)
                        wrong++;

                Assert.Equal(0, wrong);
            });
        }

        [Fact]
        public void IdsBeyondMaxIdAreRejected()
        {
            var uf = new ConcurrentUnionFind();
            Assert.Throws<ArgumentOutOfRangeException>(() => uf.Find(ConcurrentUnionFind.MaxId + 1));
            Assert.Throws<ArgumentOutOfRangeException>(() => uf.Union(0, uint.MaxValue));
            Assert.Throws<ArgumentOutOfRangeException>(() => uf.SameSet(uint.MaxValue, 0));
            Assert.Throws<ArgumentOutOfRangeException>(() => new ConcurrentUnionFind(-1));
            Assert.Equal(32, uf.Capacity);
        }

        [Fact]
        public void ResetRestoresSingletonsAndKeepsCapacity()
        {
            var uf = new ConcurrentUnionFind();
            for (uint i = 1; i < 300; i++)
                uf.Union(i - 1, i);

            Assert.Equal(512, uf.Capacity);
            uf.Reset();
            Assert.Equal(512, uf.Capacity);
            for (uint i = 0; i < 512; i++)
                Assert.Equal(i, uf.Find(i));
            Assert.False(uf.SameSet(0, 299));
        }

        [Fact]
        public void DeepCopyIsIndependent()
        {
            var uf = new ConcurrentUnionFind();
            uf.Union(1, 2);
            var copy = uf.DeepCopy();
            Assert.Equal(uf.Capacity, copy.Capacity);
            copy.Union(2, 3);
            uf.Union(4, 500);

            Assert.Equal(1u, copy.Find(3));
            Assert.Equal(3u, uf.Find(3));
            Assert.Equal(4u, uf.Find(500));
            Assert.Equal(500u, copy.Find(500));
            Assert.True(copy.SameSet(1, 2));
        }

        /// <summary>
        /// Used from one thread, the concurrent union-find gives the answers of the naive partition, including the
        /// (parent, child) pair of every union, across many segments.
        /// </summary>
        [Theory]
        [InlineData(1, 40)]
        [InlineData(2, 500)]
        [InlineData(3, 5000)]
        public void RandomOperationsMatchThePartition(int seed, int size)
        {
            var random = new Random(seed);
            var uf = new ConcurrentUnionFind(0);
            var partition = new PartitionReference(size);

            for (var step = 0; step < 6000; step++)
            {
                var x = (uint)random.Next(size);
                var y = (uint)random.Next(size);
                switch (random.Next(100))
                {
                    case < 45:
                        partition.Union(x, y, out var p, out var c);
                        Assert.Equal((p, c), uf.Union(x, y));
                        break;
                    case < 70:
                        Assert.Equal(partition.Canonical(x), uf.Find(x));
                        break;
                    case < 99:
                        Assert.Equal(partition.Canonical(x) == partition.Canonical(y), uf.SameSet(x, y));
                        break;
                    default:
                        uf.Reset();
                        partition.Reset();
                        break;
                }
            }

            for (uint i = 0; i < size; i++)
                Assert.Equal(partition.Canonical(i), uf.Find(i));
        }

        /// <summary>
        /// Many threads union random pairs over ids that span many segments, so growth races with finds and unions.
        /// Each union must be visible to <see cref="ConcurrentUnionFind.SameSet"/> as soon as it returns, and the
        /// final partition must be the one the naive reference builds from every pair, with each class's minimum as
        /// its root.
        /// </summary>
        [Theory]
        [InlineData(11, 2000)]
        [InlineData(12, 50000)]
        public void ConcurrentUnionsProduceTheReferencePartition(int seed, int size)
        {
            var threads = Math.Max(4, Environment.ProcessorCount);
            const int unionsPerThread = 3000;
            var pairs = new (uint, uint)[threads][];
            for (var t = 0; t < threads; t++)
            {
                var random = new Random(seed * 1000 + t);
                pairs[t] = new (uint, uint)[unionsPerThread];
                for (var i = 0; i < unionsPerThread; i++)
                {
                    // Bias toward ids that grow with i, so threads keep demanding new segments while others work.
                    var bound = Math.Max(2, (int)((long)size * (i + 1) / unionsPerThread));
                    pairs[t][i] = ((uint)random.Next(bound), (uint)random.Next(bound));
                }
            }

            var uf = new ConcurrentUnionFind(1);
            RunThreads(threads, t =>
            {
                foreach (var (a, b) in pairs[t])
                {
                    var (parent, child) = uf.Union(a, b);
                    Assert.True(parent <= child);
                    Assert.True(uf.SameSet(a, b));
                    Assert.True(uf.SameSet(parent, child));
                    Assert.True(uf.Find(a) <= Math.Min(a, b));
                }
            });

            var reference = new PartitionReference(size);
            foreach (var (a, b) in pairs.SelectMany(p => p))
                reference.Union(a, b, out _, out _);

            for (uint i = 0; i < size; i++)
                Assert.Equal(reference.Canonical(i), uf.Find(i));
        }

        /// <summary>
        /// Exactly one of many racing unions of the same two classes wins and reports the displaced root; the rest
        /// see one class.
        /// </summary>
        [Fact]
        public void RacingUnionsDisplaceEachRootOnce()
        {
            const int size = 4096;
            var uf = new ConcurrentUnionFind(size);
            var displaced = new ConcurrentBag<uint>();
            var random = new Random(5);
            var pairs = Enumerable.Range(0, size * 2).Select(_ => ((uint)random.Next(size), (uint)random.Next(size))).ToArray();

            RunThreads(8, _ =>
            {
                foreach (var (a, b) in pairs)
                {
                    var (parent, child) = uf.Union(a, b);
                    if (parent != child)
                        displaced.Add(child);
                }
            });

            var reference = new PartitionReference(size);
            var expected = new List<uint>();
            foreach (var (a, b) in pairs)
                if (reference.Union(a, b, out _, out var child))
                    expected.Add(child);

            // Every non-minimum member of a class was displaced exactly once, whichever thread did it.
            var actual = displaced.OrderBy(x => x).ToArray();
            Assert.Equal(actual.Length, actual.Distinct().Count());
            var nonRoots = Enumerable.Range(0, size).Select(i => (uint)i).Where(i => reference.Canonical(i) != i).ToArray();
            Assert.Equal(nonRoots, actual);
            Assert.Equal(expected.Count, actual.Length);
        }

        /// <summary>
        /// A reset racing with unions linearizes: afterward the structure is a valid union-by-min forest whose classes
        /// only join ids that some union connected.
        /// </summary>
        [Fact]
        public void ResetRacingWithUnionsLeavesAValidForest()
        {
            const int size = 3000;
            var uf = new ConcurrentUnionFind(size);
            var random = new Random(9);
            var pairs = Enumerable.Range(0, size).Select(_ => ((uint)random.Next(size), (uint)random.Next(size))).ToArray();

            RunThreads(5, t =>
            {
                if (t == 0)
                {
                    for (var i = 0; i < 50; i++)
                        uf.Reset();
                    return;
                }

                for (var round = 0; round < 20; round++)
                    foreach (var (a, b) in pairs)
                        uf.Union(a, b);
            });

            var reference = new PartitionReference(size);
            foreach (var (a, b) in pairs)
                reference.Union(a, b, out _, out _);

            for (uint i = 0; i < size; i++)
            {
                var root = uf.Find(i);
                Assert.True(root <= i);
                Assert.Equal(root, uf.Find(root));
                Assert.Equal(reference.Canonical(i), reference.Canonical(root));
            }
        }

        /// <summary>
        /// egglog's reset "blocks until all threads have finished their current operations"
        /// (<c>concurrent/mod.rs:52-57</c>): it waits for an operation in flight, an operation that arrives meanwhile
        /// waits for the reset, and that operation then sees the reset state.
        /// </summary>
        [Fact]
        public void ResetWaitsForOperationsInFlightAndHoldsOffNewOnes()
        {
            var uf = new ConcurrentUnionFind();
            uf.Union(1, 2);
            var flag = typeof(ConcurrentUnionFind).GetField("resetting", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.NotNull(flag);

            using var entered = new ManualResetEventSlim(false);
            using var release = new ManualResetEventSlim(false);
            var holder = ThreadOperationCountersTests.HoldInsideOperation(entered, release);
            try
            {
                Assert.True(entered.Wait(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
                var resetter = new Thread(uf.Reset) { IsBackground = true };
                resetter.Start();
                Assert.True(SpinWait.SpinUntil(() => (bool)flag.GetValue(uf)!, TimeSpan.FromSeconds(10)));
                Assert.False(resetter.Join(TimeSpan.FromMilliseconds(200)));

                var found = uint.MaxValue;
                var finder = new Thread(() => found = uf.Find(2)) { IsBackground = true };
                finder.Start();
                Assert.False(finder.Join(TimeSpan.FromMilliseconds(200)));

                release.Set();
                Assert.True(resetter.Join(TimeSpan.FromSeconds(10)));
                Assert.True(finder.Join(TimeSpan.FromSeconds(10)));
                Assert.Equal(2u, found);
                Assert.False((bool)flag.GetValue(uf)!);
            }
            finally
            {
                release.Set();
                holder.Join();
            }
        }

        /// <summary>
        /// A deep copy runs as one operation, so it sees a reset wholly or not at all. One thread repeatedly resets
        /// and then links every id to 0 from the top down, so the linked ids always form a suffix of the range, and a
        /// copy taken during the linking sees a suffix too. A copy that overlapped the in-place fill (which runs from
        /// the bottom up) would see a linked id below an unlinked one.
        /// </summary>
        [Fact]
        public void DeepCopyNeverSeesAPartialReset()
        {
            const int size = 1 << 16;
            var uf = new ConcurrentUnionFind(size);
            var stop = 0;
            var copies = 0;
            var torn = 0;

            RunThreads(2, t =>
            {
                if (t == 0)
                {
                    for (var round = 0; round < 40; round++)
                    {
                        uf.Reset();
                        for (var i = size - 1; i > 0; i--)
                            uf.Union((uint)i, 0);
                    }

                    Volatile.Write(ref stop, 1);
                    return;
                }

                while (Volatile.Read(ref stop) == 0)
                {
                    var copy = uf.DeepCopy();
                    var seenLinked = false;
                    for (uint i = 1; i < size; i++)
                    {
                        var root = copy.Find(i);
                        Assert.True(root == 0 || root == i);
                        if (root == 0)
                        {
                            seenLinked = true;
                        }
                        else if (seenLinked)
                        {
                            torn++;
                            break;
                        }
                    }

                    copies++;
                }
            });

            Assert.True(copies > 0);
            Assert.Equal(0, torn);
        }

    }

}

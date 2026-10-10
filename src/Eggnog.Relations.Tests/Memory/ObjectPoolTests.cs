using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Xunit;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// Tests for <see cref="ObjectPool{T, TPolicy}"/> and the standard policies, including ports of egglog's
    /// core-relations/src/pool/tests.rs.
    /// </summary>
    public class ObjectPoolTests
    {

        /// <summary>
        /// Port of <c>pooled_does_not_drop</c>. Every object handed out is cleared; an object whose clear withdraws
        /// its reuse request is dropped rather than stored, so the third rent creates a fresh object (egglog counts
        /// three drops for three objects).
        /// </summary>
        [Fact]
        public void PooledDoesNotDrop()
        {
            var pool = new ObjectPool<PoolDropper, PoolDropperPolicy>();
            var start = PoolDropperPolicy.Created;

            var d1 = pool.Rent();
            var d2 = pool.Rent();
            Assert.True(d1.Cleared);
            Assert.True(d2.Cleared);

            d1.Reuse = true;
            d1.Cleared = false;
            pool.Return(d1);
            Assert.True(d1.Cleared);
            Assert.Equal(0, pool.CountOnCurrentThread);

            var d3 = pool.Rent();
            Assert.True(d3.Cleared);
            Assert.NotSame(d1, d3);
            Assert.Equal(start + 3, PoolDropperPolicy.Created);
        }

        /// <summary>
        /// The counterpart of egglog's <c>refcount</c> test: an object the policy declines (there, an <c>Rc</c> that is
        /// still shared) is dropped on return, not pooled, and not cleared.
        /// </summary>
        [Fact]
        public void DeclinedObjectsAreDroppedUncleared()
        {
            var pool = new ObjectPool<PoolDropper, PoolDropperPolicy>();
            var d = pool.Rent();
            d.Cleared = false;

            pool.Return(d);

            Assert.False(d.Cleared);
            Assert.Equal(0, pool.CountOnCurrentThread);
            Assert.NotSame(d, pool.Rent());
        }

        [Fact]
        public void ReturnedListIsRentedAgainEmpty()
        {
            var pool = new ObjectPool<List<uint>, ListPoolPolicy<uint>>();
            var list = new List<uint>(1000);
            list.AddRange(new uint[] { 1, 2, 3 });

            pool.Return(list);
            Assert.Equal(1, pool.CountOnCurrentThread);
            Assert.Equal(1000L * sizeof(uint), pool.BytesOnCurrentThread);

            var again = pool.Rent();
            Assert.Same(list, again);
            Assert.Empty(again);
            Assert.Equal(1000, again.Capacity);
            Assert.Equal(0, pool.CountOnCurrentThread);
            Assert.Equal(0, pool.BytesOnCurrentThread);
        }

        [Fact]
        public void SmallListsAreNotPooled()
        {
            var pool = new ObjectPool<List<uint>, ListPoolPolicy<uint>>();
            var list = new List<uint>(256);
            list.Add(7);

            pool.Return(list);

            Assert.Equal(0, pool.CountOnCurrentThread);
            Assert.Single(list);
            Assert.NotSame(list, pool.Rent());
        }

        [Fact]
        public void RentIsLastInFirstOut()
        {
            var pool = new ObjectPool<List<int>, ListPoolPolicy<int>>();
            var a = new List<int>(300);
            var b = new List<int>(400);

            pool.Return(a);
            pool.Return(b);

            Assert.Same(b, pool.Rent());
            Assert.Same(a, pool.Rent());
            Assert.Empty(pool.Rent());
        }

        [Fact]
        public void ByteLimitBoundsWhatIsKept()
        {
            var pool = new ObjectPool<List<uint>, ListPoolPolicy<uint>>(2 * 1000 * sizeof(uint));
            var lists = new[] { new List<uint>(1000), new List<uint>(1000), new List<uint>(1000) };

            foreach (var list in lists)
                pool.Return(list);

            Assert.Equal(2, pool.CountOnCurrentThread);
            Assert.Equal(8000, pool.BytesOnCurrentThread);

            Assert.Same(lists[1], pool.Rent());
            Assert.Equal(4000, pool.BytesOnCurrentThread);

            pool.Return(lists[2]);
            Assert.Equal(2, pool.CountOnCurrentThread);
            Assert.Same(lists[2], pool.Rent());
        }

        [Fact]
        public void ZeroLimitKeepsOnlyWeightlessObjects()
        {
            var lists = new ObjectPool<List<uint>, ListPoolPolicy<uint>>(0);
            lists.Return(new List<uint>(1000));
            Assert.Equal(0, lists.CountOnCurrentThread);

            var dropper = new ObjectPool<PoolDropper, PoolDropperPolicy>(0);
            Assert.Equal(0, dropper.LimitBytes);
        }

        [Fact]
        public void NegativeLimitIsRejected()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new ObjectPool<List<uint>, ListPoolPolicy<uint>>(-1));
        }

        [Fact]
        public void ReturningNullIsRejected()
        {
            var pool = new ObjectPool<List<uint>, ListPoolPolicy<uint>>();
            Assert.Throws<ArgumentNullException>(() => pool.Return(null!));
        }

        [Fact]
        public void RefreshKeepsAReusableObject()
        {
            var pool = new ObjectPool<List<uint>, ListPoolPolicy<uint>>();
            var list = new List<uint>(1000) { 1, 2 };
            var original = list;

            pool.Refresh(ref list);

            Assert.Same(original, list);
            Assert.Empty(list);
        }

        [Fact]
        public void RefreshSwapsAPooledObjectForASmallOne()
        {
            var pool = new ObjectPool<List<uint>, ListPoolPolicy<uint>>();
            var big = new List<uint>(1000);
            pool.Return(big);

            var small = new List<uint>(4) { 9 };
            pool.Refresh(ref small);

            Assert.Same(big, small);
            Assert.Empty(small);
            Assert.Equal(0, pool.CountOnCurrentThread);
        }

        [Fact]
        public void RefreshKeepsASmallObjectWhenThePoolIsEmpty()
        {
            var pool = new ObjectPool<List<uint>, ListPoolPolicy<uint>>();
            var small = new List<uint>(4) { 9 };
            var original = small;

            pool.Refresh(ref small);

            Assert.Same(original, small);
            Assert.Empty(small);
        }

        [Fact]
        public void ObjectsLandInTheReturningThreadsPool()
        {
            var pool = new ObjectPool<List<uint>, ListPoolPolicy<uint>>();
            var list = pool.Rent();
            list.Capacity = 1000;

            var countOnOther = -1;
            var thread = new Thread(() =>
            {
                pool.Return(list);
                countOnOther = pool.CountOnCurrentThread;
            });
            thread.Start();
            thread.Join();

            Assert.Equal(1, countOnOther);
            Assert.Equal(0, pool.CountOnCurrentThread);
            Assert.NotSame(list, pool.Rent());
        }

        [Fact]
        public void ClearReleasesObjectsOnEveryThread()
        {
            var pool = new ObjectPool<List<uint>, ListPoolPolicy<uint>>();
            using var returned = new ManualResetEventSlim();
            using var cleared = new ManualResetEventSlim();
            var list = new List<uint>(1000);
            var countAfterClear = -1;
            List<uint>? rentedAfterClear = null;

            var thread = new Thread(() =>
            {
                pool.Return(list);
                returned.Set();
                cleared.Wait();
                countAfterClear = pool.CountOnCurrentThread;
                rentedAfterClear = pool.Rent();
            });
            thread.Start();

            returned.Wait(TestContext.Current.CancellationToken);
            pool.Return(new List<uint>(2000));
            pool.Clear();
            cleared.Set();
            thread.Join();

            Assert.Equal(0, pool.CountOnCurrentThread);
            Assert.Equal(0, pool.BytesOnCurrentThread);
            Assert.Equal(0, countAfterClear);
            Assert.NotSame(list, rentedAfterClear);
        }

        /// <summary>
        /// The stack of a thread that has exited is not kept alive by the pool, so neither is anything in it.
        /// </summary>
        [Fact]
        public void ExitedThreadsStacksAreCollected()
        {
            var pool = new ObjectPool<List<uint>, ListPoolPolicy<uint>>();
            WeakReference? pooled = null;
            var thread = new Thread(() =>
            {
                var list = new List<uint>(1000);
                pooled = new WeakReference(list);
                pool.Return(list);
            });
            thread.Start();
            thread.Join();

            for (var i = 0; i < 20 && pooled!.IsAlive; i++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
            }

            Assert.False(pooled!.IsAlive);
            pool.Clear();
        }

        /// <summary>
        /// Many threads rent, use and return objects while another thread keeps clearing the pool: no object is ever
        /// owned twice, and every rented object is cleared.
        /// </summary>
        [Fact]
        public async Task ConcurrentUseNeverHandsAnObjectToTwoOwners()
        {
            var pool = new ObjectPool<PoolProbe, PoolProbePolicy>(64 * 8);
            var failures = 0;
            using var done = new CancellationTokenSource();

            var clearer = Task.Run(() =>
            {
                while (done.IsCancellationRequested == false)
                {
                    pool.Clear();
                    Thread.Yield();
                }
            }, TestContext.Current.CancellationToken);

            var options = new ParallelOptions { MaxDegreeOfParallelism = 8, CancellationToken = TestContext.Current.CancellationToken };
            Parallel.For(0, 8, options, worker =>
            {
                var held = new List<PoolProbe>();
                var rng = new Random(worker);
                for (var i = 0; i < 20000; i++)
                {
                    if (held.Count == 0 || (held.Count < 32 && rng.Next(2) == 0))
                    {
                        var probe = pool.Rent();
                        if (Interlocked.Exchange(ref probe.Owned, 1) != 0 || probe.Cleared == false || probe.Payload != 0)
                            Interlocked.Increment(ref failures);

                        probe.Cleared = false;
                        probe.Payload = worker + 1;
                        held.Add(probe);
                    }
                    else
                    {
                        var index = rng.Next(held.Count);
                        var probe = held[index];
                        held[index] = held[^1];
                        held.RemoveAt(held.Count - 1);
                        if (probe.Payload != worker + 1)
                            Interlocked.Increment(ref failures);

                        Volatile.Write(ref probe.Owned, 0);
                        pool.Return(probe);
                    }
                }

                foreach (var probe in held)
                {
                    Volatile.Write(ref probe.Owned, 0);
                    pool.Return(probe);
                }
            });

            done.Cancel();
            await clearer;

            Assert.Equal(0, failures);
            Assert.True(pool.BytesOnCurrentThread <= pool.LimitBytes);
        }

        [Fact]
        public void HashSetPolicyFollowsCapacity()
        {
            var pool = new ObjectPool<HashSet<uint>, HashSetPoolPolicy<uint>>();

            pool.Return(new HashSet<uint>());
            Assert.Equal(0, pool.CountOnCurrentThread);

            var set = new HashSet<uint> { 1, 2, 3 };
            var capacity = set.EnsureCapacity(0);
            pool.Return(set);
            Assert.Equal(1, pool.CountOnCurrentThread);
            Assert.Equal((long)capacity * sizeof(uint), pool.BytesOnCurrentThread);

            var again = pool.Rent();
            Assert.Same(set, again);
            Assert.Empty(again);
            Assert.Equal(capacity, again.EnsureCapacity(0));
        }

        [Fact]
        public void DictionaryPolicyFollowsCapacity()
        {
            var pool = new ObjectPool<Dictionary<uint, long>, DictionaryPoolPolicy<uint, long>>();

            pool.Return(new Dictionary<uint, long>());
            Assert.Equal(0, pool.CountOnCurrentThread);

            var map = new Dictionary<uint, long> { [1] = 10, [2] = 20 };
            var capacity = map.EnsureCapacity(0);
            pool.Return(map);
            Assert.Equal((long)capacity * (sizeof(uint) + sizeof(long)), pool.BytesOnCurrentThread);

            var again = pool.Rent();
            Assert.Same(map, again);
            Assert.Empty(again);
        }

        [Fact]
        public void ListPolicyWeighsCapacityTimesElementSize()
        {
            var list = new List<long>(500);

            Assert.True(ListPoolPolicy<long>.ShouldReuse(list));
            Assert.Equal(500L * sizeof(long), ListPoolPolicy<long>.Bytes(list));
            Assert.False(ListPoolPolicy<long>.ShouldReuse(new List<long>(256)));
            Assert.True(ListPoolPolicy<long>.ShouldReuse(new List<long>(257)));
        }

    }

}

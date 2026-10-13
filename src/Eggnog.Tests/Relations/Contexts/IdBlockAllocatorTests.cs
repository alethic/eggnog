using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

using Xunit;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// Tests for <see cref="IdBlockAllocator"/>: child ids come from blocks reserved on the store's one id counter, so
    /// ids are distinct store-wide and <see cref="IdBlockAllocator.Contains"/> is exact.
    /// </summary>
    public class IdBlockAllocatorTests
    {

        /// <summary>
        /// Eight allocators on eight threads, with root mints from the same counter interleaved, hand out a million
        /// distinct ids, and each allocator contains exactly its own.
        /// </summary>
        [Fact]
        public void EightAllocatorsAndRootMintsOnEightThreadsHandOutDistinctIds()
        {
            const int threads = 8;
            const int perThread = 125_000;
            var counters = new CounterSet();
            var ids = counters.Add();
            var allocators = Enumerable.Range(0, threads).Select(_ => new IdBlockAllocator(counters, ids)).ToArray();
            var minted = new uint[threads][];
            var rootMinted = new List<uint>[threads];
            using var start = new ManualResetEventSlim();
            var workers = ConcurrencyTestThread.StartMany(threads, t =>
            {
                var mine = new uint[perThread];
                var root = new List<uint>();
                start.Wait();
                for (var i = 0; i < perThread; i++)
                {
                    // Every fifth id is a root mint straight from the counter, interleaved with the blocks.
                    if (i % 5 == 4)
                        root.Add((uint)counters.Increment(ids));

                    mine[i] = allocators[t].Next();
                }

                minted[t] = mine;
                rootMinted[t] = root;
            });

            start.Set();
            ConcurrencyTestThread.JoinAll(workers);

            var total = threads * perThread + rootMinted.Sum(r => r.Count);
            Assert.True(total >= 1_000_000);
            var seen = new bool[counters.Read(ids)];
            foreach (var id in minted.SelectMany(m => m).Concat(rootMinted.SelectMany(r => r)))
            {
                Assert.False(seen[id], $"id {id} was handed out twice");
                seen[id] = true;
            }

            // Contains is exact: each allocator owns exactly its own ids, and no root id.
            for (var t = 0; t < threads; t++)
            {
                Assert.Equal(perThread, allocators[t].Minted);
                foreach (var id in minted[t].Where((_, i) => i % 997 == 0))
                {
                    for (var u = 0; u < threads; u++)
                        Assert.Equal(u == t, allocators[u].Contains(id));
                }

                foreach (var id in rootMinted[t])
                    Assert.All(allocators, a => Assert.False(a.Contains(id)));
            }
        }

        /// <summary>
        /// One allocator called from eight threads at once hands out each id once and contains them all.
        /// </summary>
        [Fact]
        public void OneAllocatorSharedByEightThreadsHandsOutEachIdOnce()
        {
            var counters = new CounterSet();
            var ids = counters.Add();
            var allocator = new IdBlockAllocator(counters, ids);
            var minted = new uint[8][];
            using var start = new ManualResetEventSlim();
            var workers = ConcurrencyTestThread.StartMany(8, t =>
            {
                var mine = new uint[50_000];
                start.Wait();
                for (var i = 0; i < mine.Length; i++)
                    mine[i] = allocator.Next();

                minted[t] = mine;
            });

            start.Set();
            ConcurrencyTestThread.JoinAll(workers);
            var all = minted.SelectMany(m => m).ToArray();
            Assert.Equal(all.Length, all.Distinct().Count());
            Assert.Equal(400_000, allocator.Minted);
            Assert.All(all.Where((_, i) => i % 101 == 0), id => Assert.True(allocator.Contains(id)));
        }

        /// <summary>
        /// <see cref="IdBlockAllocator.Contains"/> is true exactly for the ids of the allocator's reserved blocks,
        /// minted or not, whatever other allocators and root mints take in between.
        /// </summary>
        [Fact]
        public void ContainsMatchesTheReservedBlocksExactly()
        {
            var counters = new CounterSet();
            var ids = counters.Add();
            var a = new IdBlockAllocator(counters, ids);
            var b = new IdBlockAllocator(counters, ids);
            var rng = new Random(7);
            var mintedByA = new HashSet<uint>();
            for (var i = 0; i < 20_000; i++)
            {
                switch (rng.Next(3))
                {
                    case 0:
                        mintedByA.Add(a.Next());
                        break;
                    case 1:
                        b.Next();
                        break;
                    default:
                        counters.Increment(ids);
                        break;
                }
            }

            // The reference: the union of a's reserved blocks.
            var blocks = Enumerable.Range(0, a.BlockCount).Select(i => (Start: a.BlockStart(i), End: a.BlockEnd(i))).ToArray();
            var end = counters.Read(ids) + 100;
            for (long id = 0; id < end; id++)
            {
                var expected = blocks.Any(x => x.Start <= id && id < x.End);
                Assert.Equal(expected, a.Contains((uint)id));
                if (mintedByA.Contains((uint)id))
                    Assert.True(a.Contains((uint)id));

                if (expected)
                    Assert.False(b.Contains((uint)id));
            }

            Assert.False(new IdBlockAllocator(counters, ids).Contains(0));
        }

        /// <summary>
        /// The first block holds <see cref="IdBlockAllocator.FirstBlockSize"/> ids, and each next block twice the total
        /// reserved so far, up to <see cref="IdBlockAllocator.MaxBlockSize"/>.
        /// </summary>
        [Fact]
        public void BlocksStartAtSixteenAndDoubleTheReservedTotalUpToTheCap()
        {
            var counters = new CounterSet();
            var ids = counters.Add();
            var allocator = new IdBlockAllocator(counters, ids);
            Assert.Equal(0, allocator.BlockCount);
            Assert.Equal(0, allocator.Minted);

            var first = allocator.Next();
            Assert.Equal(0u, first);
            Assert.Equal(1, allocator.BlockCount);
            Assert.Equal(IdBlockAllocator.FirstBlockSize, allocator.BlockEnd(0) - allocator.BlockStart(0));

            for (var i = 1; i < 20_000; i++)
                Assert.Equal((uint)i, allocator.Next());

            Assert.Equal(20_000, allocator.Minted);
            long reserved = 0;
            var sizes = new List<long>();
            for (var i = 0; i < allocator.BlockCount; i++)
            {
                var size = allocator.BlockEnd(i) - allocator.BlockStart(i);
                Assert.Equal(Math.Clamp(2 * reserved, IdBlockAllocator.FirstBlockSize, IdBlockAllocator.MaxBlockSize), size);
                sizes.Add(size);
                reserved += size;
            }

            Assert.Equal(new long[] { 16, 32, 96, 288, 864, 2592, 4096, 4096, 4096, 4096 }, sizes.Take(10));
            Assert.Equal(reserved, counters.Read(ids));
        }

        /// <summary>
        /// A block that would pass the union-find's maximum id is not reserved, and every mint that needs it throws
        /// <see cref="OverflowException"/>.
        /// </summary>
        [Fact]
        public void ABlockPastTheUnionFindMaximumOverflows()
        {
            var counters = new CounterSet();
            var ids = counters.Add();
            var maxId = UnionFind.MaxId;
            counters.IncrementBy(ids, maxId - 20);
            var allocator = new IdBlockAllocator(counters, ids);

            // The first block [MaxId - 20, MaxId - 4) fits.
            for (var i = 0; i < IdBlockAllocator.FirstBlockSize; i++)
                Assert.Equal(maxId - 20 + (uint)i, allocator.Next());

            // The second, 32 ids from MaxId - 4, would pass MaxId.
            Assert.Throws<OverflowException>(() => allocator.Next());
            Assert.Throws<OverflowException>(() => allocator.Next());
            Assert.Equal(IdBlockAllocator.FirstBlockSize, allocator.Minted);
            Assert.Equal(1, allocator.BlockCount);
        }

        /// <summary>
        /// <see cref="CounterSet.IncrementBy"/> returns the first of the values it reserves and refuses a negative
        /// count or an unknown counter.
        /// </summary>
        [Fact]
        public void IncrementByReservesConsecutiveValues()
        {
            var counters = new CounterSet();
            var c = counters.Add();
            Assert.Equal(0, counters.IncrementBy(c, 10));
            Assert.Equal(10, counters.Increment(c));
            Assert.Equal(11, counters.IncrementBy(c, 0));
            Assert.Equal(11, counters.Read(c));
            Assert.Throws<ArgumentOutOfRangeException>(() => counters.IncrementBy(c, -1));
            Assert.Throws<KeyNotFoundException>(() => counters.IncrementBy(new CounterId(5), 1));
        }

    }

}

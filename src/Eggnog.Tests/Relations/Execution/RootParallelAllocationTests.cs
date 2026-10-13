using System;

using Xunit;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// What layered execution costs a root run: a parallel root run never recycles the trie nodes it creates at
    /// positions 0 and 1 (the garbage collector reclaims them), so every such node is allocated per frame. A concurrent
    /// root node must take at most 64 bytes, what a node without layer support takes, so that layers add nothing to a
    /// root run's allocation.
    /// </summary>
    [Collection(AllocationProbeCollection.Name)]
    public class RootParallelAllocationTests
    {

        [Fact]
        public void ARootRunsConcurrentTrieNodeIsNoLargerThanBeforeLayers()
        {
            var pool = new TrieNodePool(true);
            for (var i = 0; i < 100; i++)
                pool.Rent(Subset.Dense(0, 10));

            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var i = 0; i < 1000; i++)
                pool.Rent(Subset.Dense(0, 10));

            var perNode = (GC.GetAllocatedBytesForCurrentThread() - before) / 1000.0;
            Assert.True(perNode <= 64, $"a concurrent root trie node takes {perNode:0.0} bytes (64 before layered execution)");
        }

    }

}

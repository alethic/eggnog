using System;

using Xunit;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// Review probe (root cost, colored-contexts spec §1 item 8, §14.1): a parallel root run never recycles the trie nodes
    /// it creates at positions 0 and 1 (the garbage collector reclaims them), so every such node is allocated per frame.
    /// Before layered execution a node took 64 bytes; a root run should allocate nothing more than it did.
    /// </summary>
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

using System;
using System.Linq;
using System.Reflection;

using Xunit;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// What <see cref="ConcurrentUnionFind"/> allocates: a reset re-initializes the slots in place.
    /// </summary>
    [Collection(AllocationProbeCollection.Name)]
    public class ConcurrentUnionFindAllocationTests
    {

        /// <summary>
        /// egglog's reset re-initializes the buffer in place (<c>union-find/src/concurrent/buffer.rs:91-98</c>), and so
        /// does <see cref="ConcurrentUnionFind.Reset"/>: a whole new set of segments on every reset would allocate (on
        /// the large object heap) as much as the union-find itself for a large one, and transiently double its
        /// footprint.
        /// </summary>
        [Fact]
        public void ConcurrentResetDoesNotReallocateTheSlots()
        {
            var uf = new ConcurrentUnionFind(1 << 20);
            uf.Reset();

            var before = GC.GetAllocatedBytesForCurrentThread();
            uf.Reset();
            var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

            Assert.True(allocated < 64 * 1024, $"Reset allocated {allocated} bytes for a capacity of {uf.Capacity}.");
        }

    }

}

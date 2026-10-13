using System;

using Xunit;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// What a released <see cref="SubsetBuffer"/> keeps for its next use: its free-list heads, and an array too short
    /// for the arena pool, which would otherwise be dropped and allocated again by every rebuild.
    /// </summary>
    [Collection(IndexStressCollection.Name)]
    public class SubsetBufferReuseTests
    {

        static readonly int[] FirstRun = [1, 2];

        static readonly int[] SecondRun = [5];

        static ObjectPool<int[], SubsetArenaPolicy> Arenas => SharedPool<int[], SubsetArenaPolicy>.Instance;

        /// <summary>
        /// Allocates two runs and grows one past its block, which moves it and frees the block, so the free lists are
        /// created; returns the arena's array.
        /// </summary>
        static int[] Fill(ref SubsetBuffer buffer)
        {
            // Runs from arrays: a collection literal here would allocate a span's backing store per call.
            var run = buffer.NewRun(FirstRun);
            buffer.NewRun(SecondRun);
            run = run.AddRowSorted(3, ref buffer);
            run.AddRowSorted(4, ref buffer);
            return buffer.Rows;
        }

        [Fact]
        public void ReleaseKeepsAShortArrayAndHandsALongOneBack()
        {
            Arenas.Clear();
            var buffer = SubsetBuffer.Create();
            var rows = Fill(ref buffer);
            Assert.True(rows.Length <= PooledArray.MaxUnpooledLength);
            buffer.Release();
            Assert.Equal(0, buffer.Length);
            Assert.Same(rows, buffer.Rows);

            // The emptied free lists hand out no block of the old arena: allocation starts at zero again.
            Assert.Equal(0, buffer.Allocate(2));

            var big = SubsetBuffer.Create();
            big.Allocate(PooledArray.MaxUnpooledLength * 2);
            var count = Arenas.CountOnCurrentThread;
            big.Release();
            Assert.Empty(big.Rows);
            Assert.Equal(count + 1, Arenas.CountOnCurrentThread);
            buffer.Release();
        }

        [Fact]
        public void ReleasedArenaRefillsWithoutAllocating()
        {
            var buffer = SubsetBuffer.Create();
            for (var i = 0; i < 20; i++)
            {
                Fill(ref buffer);
                buffer.Release();
            }

            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var i = 0; i < 1000; i++)
            {
                Fill(ref buffer);
                buffer.Release();
            }

            var perCycle = (GC.GetAllocatedBytesForCurrentThread() - before) / 1000.0;
            Assert.True(perCycle < 8, $"{perCycle:0.0} bytes per fill and release");
        }

        [Fact]
        public void AShortKeptArrayGivesWayToARecycledLongOne()
        {
            Arenas.Clear();
            var buffer = SubsetBuffer.Create();
            var shortRows = Fill(ref buffer);
            buffer.Release();
            Assert.Same(shortRows, buffer.Rows);

            var big = SubsetBuffer.Create();
            big.Allocate(PooledArray.MaxUnpooledLength * 2);
            var recycled = big.Rows;
            big.Release();

            // The empty arena needs more than its short array holds: it takes the pool's array before allocating.
            Assert.Equal(0, buffer.Allocate(PooledArray.MaxUnpooledLength * 2));
            Assert.Same(recycled, buffer.Rows);
            buffer.Release();
            Assert.Empty(buffer.Rows);
        }

    }

}

using System;
using System.Buffers;

using Xunit;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// Tests for staging row buffers (<see cref="RowBuffer.ForStaging(int)"/>): growth returns the storage it replaces
    /// to the shared array pool at once, where an ordinary buffer leaves it to the garbage collector, and the rows
    /// survive every growth. The pool checks rent on the test's own thread right after the growth: the shared pool
    /// keeps the array returned last in that thread's slot for its size, and the next rent of that size on the thread
    /// takes it.
    /// </summary>
    [Collection(AllocationProbeCollection.Name)]
    public class StagingRowBufferPoolTests
    {

        /// <summary>
        /// The pooled length a one-cell buffer grows through before it doubles again: the smallest the pool holds.
        /// </summary>
        const int PooledLength = 512;

        [Fact]
        public void StagingGrowthReturnsTheOldStorageToThePool()
        {
            var buffer = RowBuffer.ForStaging(1);
            Assert.True(buffer.ReleasesGrownStorage);
            var old = FillToPooledLength(buffer);

            buffer.AddRow([PooledLength]);
            Assert.NotSame(old, buffer.Data);

            var rented = ArrayPool<uint>.Shared.Rent(PooledLength);
            try
            {
                Assert.Same(old, rented);
            }
            finally
            {
                ArrayPool<uint>.Shared.Return(rented);
            }

            AssertRowsAreTheirIds(buffer, PooledLength + 1);
            buffer.Dispose();
        }

        [Fact]
        public void OrdinaryGrowthLeavesTheOldStorageAlone()
        {
            var buffer = new RowBuffer(1);
            Assert.False(buffer.ReleasesGrownStorage);
            var old = FillToPooledLength(buffer);

            buffer.AddRow([PooledLength]);
            var rented = ArrayPool<uint>.Shared.Rent(PooledLength);
            try
            {
                Assert.NotSame(old, rented);
            }
            finally
            {
                ArrayPool<uint>.Shared.Return(rented);
            }

            // Whoever still holds the old storage keeps reading the rows it had.
            for (var i = 0; i < PooledLength; i++)
                Assert.Equal((uint)i, old[i]);

            buffer.Dispose();
        }

        [Fact]
        public void StagingReserveReturnsTheOldStorageToThePool()
        {
            var buffer = RowBuffer.ForStaging(1);
            var old = FillToPooledLength(buffer);

            buffer.Reserve(PooledLength + 1);
            var rented = ArrayPool<uint>.Shared.Rent(PooledLength);
            try
            {
                Assert.Same(old, rented);
            }
            finally
            {
                ArrayPool<uint>.Shared.Return(rented);
            }

            AssertRowsAreTheirIds(buffer, PooledLength);
            buffer.Dispose();
        }

        [Fact]
        public void AStagedRowCopiedFromTheBufferItselfSurvivesTheGrowth()
        {
            var buffer = RowBuffer.ForStaging(2);
            var rows = 0;
            while (buffer.Data.Length < PooledLength || buffer.Count < buffer.Capacity)
            {
                buffer.AddRow([(uint)rows, (uint)rows + 1]);
                rows++;
            }

            // The source row lives in the pooled storage the growth replaces and releases. Debug builds poison released
            // storage, so a copy taken after the release would read poison.
            Assert.Equal(PooledLength, buffer.Data.Length);
            buffer.AddRow(buffer.GetRow(3));
            Assert.Equal(new uint[] { 3, 4 }, buffer.GetRow(rows).ToArray());
            buffer.Dispose();
        }

        [Fact]
        public void AStagingBufferAllocatesNothingBeforeItsFirstRow()
        {
            Assert.Same(Array.Empty<uint>(), RowBuffer.ForStaging(5).Data);
            Assert.Throws<ArgumentOutOfRangeException>(() => RowBuffer.ForStaging(0));
        }

        [Fact]
        public void MutationBufferStagesIntoStagingBuffers()
        {
            var state = new PendingState(new ShardData(4));
            var buffer = new MutationBuffer(state, 2, 1, true);
            for (uint i = 0; i < 100; i++)
                buffer.StageInsert([i, i]);

            buffer.Flush();
            var seen = 0;
            for (var shard = 0; shard < state.ShardCount; shard++)
            {
                foreach (var rows in state.RowQueue(shard))
                {
                    Assert.True(rows.ReleasesGrownStorage);
                    seen += rows.Count;
                }
            }

            Assert.Equal(100, seen);
            state.Clear();
        }

        /// <summary>
        /// Adds rows of one cell, each holding its id, until the buffer's storage is exactly
        /// <see cref="PooledLength"/> cells and full, and returns that storage.
        /// </summary>
        static uint[] FillToPooledLength(RowBuffer buffer)
        {
            for (var i = 0; i < PooledLength; i++)
                buffer.AddRow([(uint)i]);

            Assert.Equal(PooledLength, buffer.Data.Length);
            return buffer.Data;
        }

        /// <summary>
        /// Checks that the buffer holds <paramref name="count"/> one-cell rows, each holding its id.
        /// </summary>
        static void AssertRowsAreTheirIds(RowBuffer buffer, int count)
        {
            Assert.Equal(count, buffer.Count);
            for (var i = 0; i < count; i++)
                Assert.Equal((uint)i, buffer.GetRow(i)[0]);
        }

    }

}

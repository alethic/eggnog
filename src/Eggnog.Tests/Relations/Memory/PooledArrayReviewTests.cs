using System.Buffers;

using Xunit;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// Review tests for <see cref="PooledArray"/>'s retention policy against egglog's per-thread byte limits.
    /// </summary>
    public class PooledArrayReviewTests
    {

        /// <summary>
        /// egglog keeps row storage (<c>vec_cell_vals: Vec&lt;Cell&lt;Value&gt;&gt;</c>) in a pool capped at 32 MiB per
        /// thread; <c>PoolState::push</c> (pool/mod.rs:182-192) drops any vector that would push the pool past the cap,
        /// so a single vector over 32 MiB is freed when its buffer is dropped. Here the disposed storage goes back to
        /// <see cref="ArrayPool{T}.Shared"/> regardless of size and stays reachable from the process-wide pool.
        /// </summary>
        [Fact]
        public void DisposedRowStorageOverTheVecCellLimitIsNotRetained()
        {
            var cells = (int)(PoolLimits.VecCellValues / sizeof(uint)) + 1;
            var rows = new RowBuffer(1, cells);
            var storage = rows.Data;
            Assert.True((long)storage.Length * sizeof(uint) > PoolLimits.VecCellValues);

            rows.Dispose();

            var again = ArrayPool<uint>.Shared.Rent(storage.Length);
            var retained = ReferenceEquals(storage, again);
            if (retained == false)
                ArrayPool<uint>.Shared.Return(again);

            Assert.False(retained, "A 64 MiB row buffer was kept by the shared pool after Dispose; egglog frees it.");
        }

    }

}

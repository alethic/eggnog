using System;
using System.Collections.Generic;

using Xunit;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// Tests for <see cref="PoolSet"/> and <see cref="PoolLimits"/>.
    /// </summary>
    public class PoolSetTests
    {

        [Fact]
        public void CreatedPoolsAreRegisteredAndClearedTogether()
        {
            var set = new PoolSet();
            var lists = set.CreatePool<List<uint>, ListPoolPolicy<uint>>(PoolLimits.VecValues);
            var sets = set.CreatePool<HashSet<uint>, HashSetPoolPolicy<uint>>(PoolLimits.ColumnIndex);
            Assert.Equal(2, set.Count);
            Assert.Equal(PoolLimits.VecValues, lists.LimitBytes);

            lists.Return(new List<uint>(1000));
            sets.Return(new HashSet<uint> { 1 });
            Assert.Equal(1, lists.CountOnCurrentThread);
            Assert.Equal(1, sets.CountOnCurrentThread);

            set.Clear();

            Assert.Equal(0, lists.CountOnCurrentThread);
            Assert.Equal(0, sets.CountOnCurrentThread);
        }

        [Fact]
        public void RegisteredPoolsAreCleared()
        {
            var set = new PoolSet();
            var pool = new ObjectPool<List<int>, ListPoolPolicy<int>>();
            set.Register(pool);
            pool.Return(new List<int>(1000));

            set.Clear();

            Assert.Equal(0, pool.CountOnCurrentThread);
        }

        [Fact]
        public void RegisteringNullIsRejected()
        {
            Assert.Throws<ArgumentNullException>(() => new PoolSet().Register(null!));
        }

        [Fact]
        public void SharedSetExists()
        {
            Assert.Same(PoolSet.Shared, PoolSet.Shared);
        }

        [Fact]
        public void LimitsMatchEgglog()
        {
            Assert.Equal(1L << 25, PoolLimits.VecValues);
            Assert.Equal(1L << 25, PoolLimits.VecCellValues);
            Assert.Equal(1L << 25, PoolLimits.Rows);
            Assert.Equal(1L << 25, PoolLimits.StagedOutputs);
            Assert.Equal(4L << 20, PoolLimits.CachedSubsets);
            Assert.Equal(1L << 20, PoolLimits.OffsetVec);
        }

    }

}

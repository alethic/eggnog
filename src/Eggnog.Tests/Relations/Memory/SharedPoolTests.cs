using System.Collections.Generic;

using Xunit;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// Tests for <see cref="SharedPool{T, TPolicy}"/>, the counterpart of egglog's type-directed
    /// <c>PoolSet::get_pool</c>. The tests share one engine-wide pool, so they live in one class and run one at a time.
    /// </summary>
    public class SharedPoolTests
    {

        [Fact]
        public void EachTypeAndPolicyHasExactlyOnePool()
        {
            var pool = SharedPool<PoolProbe, PoolProbePolicy>.Instance;

            Assert.Same(pool, SharedPool<PoolProbe, PoolProbePolicy>.Instance);
            Assert.Equal(PoolProbePolicy.LimitBytes, pool.LimitBytes);
            Assert.NotSame((object)pool, SharedPool<List<uint>, ListPoolPolicy<uint>>.Instance);
        }

        [Fact]
        public void StandardPoliciesUseEgglogsLimits()
        {
            Assert.Equal(PoolLimits.VecValues, SharedPool<List<uint>, ListPoolPolicy<uint>>.Instance.LimitBytes);
            Assert.Equal(PoolLimits.TupleIndexes, SharedPool<HashSet<uint>, HashSetPoolPolicy<uint>>.Instance.LimitBytes);
            Assert.Equal(PoolLimits.ColumnIndex, SharedPool<Dictionary<uint, uint>, DictionaryPoolPolicy<uint, uint>>.Instance.LimitBytes);
        }

        /// <summary>
        /// An object rented through the shared pool and returned through its instance (as two modules would) goes back
        /// to the one pool and is rented again, cleared.
        /// </summary>
        [Fact]
        public void ObjectsComeBackToTheOnePool()
        {
            var probe = SharedPool<PoolProbe, PoolProbePolicy>.Rent();
            probe.Payload = 5;
            probe.Cleared = false;

            SharedPool<PoolProbe, PoolProbePolicy>.Instance.Return(probe);
            var again = SharedPool<PoolProbe, PoolProbePolicy>.Rent();

            Assert.Same(probe, again);
            Assert.True(again.Cleared);
            Assert.Equal(0, again.Payload);
            SharedPool<PoolProbe, PoolProbePolicy>.Return(again);
        }

        [Fact]
        public void RefreshClearsInPlace()
        {
            var probe = new PoolProbe { Payload = 3, Cleared = false };
            var original = probe;

            SharedPool<PoolProbe, PoolProbePolicy>.Refresh(ref probe);

            Assert.Same(original, probe);
            Assert.True(probe.Cleared);
            Assert.Equal(0, probe.Payload);
        }

        /// <summary>
        /// The pool keeps at most the policy's limit per thread, and clearing the shared set empties it.
        /// </summary>
        [Fact]
        public void LimitComesFromThePolicyAndTheSharedSetClearsThePool()
        {
            var pool = SharedPool<PoolProbe, PoolProbePolicy>.Instance;

            for (var i = 0; i < 40; i++)
                SharedPool<PoolProbe, PoolProbePolicy>.Return(new PoolProbe());

            Assert.Equal(PoolProbePolicy.LimitBytes / 8, pool.CountOnCurrentThread);
            Assert.Equal(PoolProbePolicy.LimitBytes, pool.BytesOnCurrentThread);

            PoolSet.Shared.Clear();

            Assert.Equal(0, pool.CountOnCurrentThread);
            Assert.Equal(0, pool.BytesOnCurrentThread);
        }

    }

}

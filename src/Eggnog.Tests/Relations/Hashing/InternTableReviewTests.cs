using System.Linq;

using Xunit;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// Adversarial review tests for <see cref="InternTable{TKey}"/>'s key hash.
    /// </summary>
    public class InternTableReviewTests
    {

        /// <summary>
        /// egglog interns every <c>i64</c>/<c>u64</c> base value that does not fit in 31 bits
        /// (core-relations/src/base_values/unboxed.rs, <c>impl_medium_base_value</c>) and hashes it with
        /// <c>FxHasher::write_u64</c>, a bijection on 64-bit values, so distinct keys always get distinct hashes. The
        /// port first folds the key to 32 bits with <see cref="long.GetHashCode"/> (<c>lo ^ hi</c>), so every value of
        /// the form <c>k * (2^32 + 1)</c> gets the same 64-bit hash: same shard, same probe start, same tag, and each
        /// intern of such a key compares against every earlier one (quadratic interning).
        /// </summary>
        [Fact]
        public void DistinctLongKeysGetDistinctHashes()
        {
            const long stride = 0x1_0000_0001L;
            var hashes = Enumerable.Range(1, 4096).Select(k => InternTable<long>.HashKey(k * stride)).Distinct().Count();
            Assert.Equal(4096, hashes);
        }

    }

}

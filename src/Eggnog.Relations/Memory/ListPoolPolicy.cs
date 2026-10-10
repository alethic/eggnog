using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace Eggnog.Relations
{

    /// <summary>
    /// Pool policy for <see cref="List{T}"/>: egglog's <c>impl Clear for Vec&lt;T&gt;</c>
    /// (core-relations/src/pool/mod.rs:48-58). A list is kept only when its capacity exceeds
    /// <see cref="PooledArray.MaxUnpooledLength"/>, and weighs its capacity times the element size.
    /// </summary>
    /// <remarks>
    /// Its shared pool (<see cref="SharedPool{T, TPolicy}"/>) holds up to 32 MiB per thread, the limit egglog gives
    /// its large vector pools (<c>vec_vals</c>, <c>rows</c>). egglog's 1 MiB vector pools (<c>constraints</c>,
    /// <c>instrs</c>, <c>instr_indexes</c>) need a policy of their own, so that they get their own pool and limit:
    /// <c>Vec&lt;u32&gt;</c> and <c>Vec&lt;Value&gt;</c> are distinct pools in egglog but would both be
    /// <c>List&lt;uint&gt;</c> here.
    /// </remarks>
    /// <typeparam name="TItem">The element type.</typeparam>
    internal readonly struct ListPoolPolicy<TItem> : IPoolPolicy<List<TItem>>
    {

        /// <inheritdoc />
        public static List<TItem> Create() => new();

        /// <inheritdoc />
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Clear(List<TItem> item) => item.Clear();

        /// <inheritdoc />
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool ShouldReuse(List<TItem> item) => item.Capacity > PooledArray.MaxUnpooledLength;

        /// <inheritdoc />
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static long Bytes(List<TItem> item) => (long)item.Capacity * Unsafe.SizeOf<TItem>();

        /// <inheritdoc />
        public static long LimitBytes => PoolLimits.VecValues;

    }

}

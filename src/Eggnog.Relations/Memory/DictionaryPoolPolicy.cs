using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace Eggnog.Relations
{

    /// <summary>
    /// Pool policy for <see cref="Dictionary{TKey, TValue}"/>: egglog's <c>impl Clear for HashMap&lt;K, V&gt;</c>
    /// (core-relations/src/pool/mod.rs:105-115). A map is kept whenever it has any capacity, and weighs its capacity
    /// times the size of a key-value pair. <see cref="Dictionary{TKey, TValue}.Clear"/> keeps the map's storage.
    /// </summary>
    /// <typeparam name="TKey">The key type.</typeparam>
    /// <typeparam name="TValue">The value type.</typeparam>
    internal readonly struct DictionaryPoolPolicy<TKey, TValue> : IPoolPolicy<Dictionary<TKey, TValue>>
        where TKey : notnull
    {

        /// <inheritdoc />
        public static Dictionary<TKey, TValue> Create() => new();

        /// <inheritdoc />
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Clear(Dictionary<TKey, TValue> item) => item.Clear();

        /// <inheritdoc />
        /// <remarks><see cref="Dictionary{TKey, TValue}.EnsureCapacity"/> with zero only reads the current capacity.</remarks>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool ShouldReuse(Dictionary<TKey, TValue> item) => item.EnsureCapacity(0) > 0;

        /// <inheritdoc />
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static long Bytes(Dictionary<TKey, TValue> item) =>
            (long)item.EnsureCapacity(0) * (Unsafe.SizeOf<TKey>() + Unsafe.SizeOf<TValue>());

        /// <inheritdoc />
        /// <remarks>
        /// egglog pools no plain hash map; this is the 1 MiB it gives its pooled map (<c>column_index</c>). A module
        /// that needs a larger limit, such as the 32 MiB of <c>staged_outputs</c>, declares its own policy.
        /// </remarks>
        public static long LimitBytes => PoolLimits.ColumnIndex;

    }

}

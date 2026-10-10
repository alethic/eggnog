using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace Eggnog.Relations
{

    /// <summary>
    /// Pool policy for <see cref="HashSet{T}"/>: egglog's <c>impl Clear for HashSet&lt;T&gt;</c>
    /// (core-relations/src/pool/mod.rs:81-91). A set is kept whenever it has any capacity, and weighs its capacity
    /// times the element size. <see cref="HashSet{T}.Clear"/> keeps the set's storage, as hashbrown's clear does.
    /// </summary>
    /// <typeparam name="TItem">The element type.</typeparam>
    internal readonly struct HashSetPoolPolicy<TItem> : IPoolPolicy<HashSet<TItem>>
    {

        /// <inheritdoc />
        public static HashSet<TItem> Create() => new();

        /// <inheritdoc />
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Clear(HashSet<TItem> item) => item.Clear();

        /// <inheritdoc />
        /// <remarks><see cref="HashSet{T}.EnsureCapacity"/> with zero only reads the current capacity.</remarks>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool ShouldReuse(HashSet<TItem> item) => item.EnsureCapacity(0) > 0;

        /// <inheritdoc />
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static long Bytes(HashSet<TItem> item) => (long)item.EnsureCapacity(0) * Unsafe.SizeOf<TItem>();

        /// <inheritdoc />
        /// <remarks>
        /// egglog pools no plain hash set; this is the 1 MiB it gives its pooled hash table of index entries
        /// (<c>tuple_indexes</c>). A module that needs a larger limit declares its own policy.
        /// </remarks>
        public static long LimitBytes => PoolLimits.TupleIndexes;

    }

}

using System.Runtime.CompilerServices;

namespace Eggnog.Relations
{

    /// <summary>
    /// Pool policy for the hash table of a <see cref="TupleIndexShard"/>: egglog's <c>tuple_indexes</c> pool of
    /// <c>HashTable&lt;TableEntry&lt;BufferedSubset&gt;&gt;</c> (core-relations/src/pool/mod.rs:93-103, 424-446; 1 MiB per
    /// thread), which every <c>SubsetTable</c> takes its hash table from (core-relations/src/hash_index/mod.rs:123-144).
    /// </summary>
    /// <remarks>
    /// A table is kept whenever it has slots, as egglog keeps a hash table with any capacity, and weighs its slots plus
    /// their control bytes. <see cref="RawHashTable{TEntry}.Clear"/> keeps the slots, so a table rented again holds as
    /// many keys as it last grew to without rehashing. Clearing costs a pass over the control bytes; the entries hold no
    /// references and are not touched.
    /// </remarks>
    internal readonly struct TupleIndexHashPolicy : IPoolPolicy<RawHashTable<TupleIndexEntry>>
    {

        /// <inheritdoc />
        public static RawHashTable<TupleIndexEntry> Create() => new();

        /// <inheritdoc />
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Clear(RawHashTable<TupleIndexEntry> item) => item.Clear();

        /// <inheritdoc />
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool ShouldReuse(RawHashTable<TupleIndexEntry> item) => item.SlotCount > 0;

        /// <inheritdoc />
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static long Bytes(RawHashTable<TupleIndexEntry> item) => (long)item.SlotCount * (Unsafe.SizeOf<TupleIndexEntry>() + 1);

        /// <inheritdoc />
        public static long LimitBytes => PoolLimits.TupleIndexes;

    }

}

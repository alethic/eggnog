using System.Runtime.CompilerServices;

namespace Eggnog.Relations
{

    /// <summary>
    /// Pool policy for the hash table of a <see cref="ColumnIndexShard"/>: egglog's <c>column_index</c> pool of
    /// <c>IndexMap&lt;Value, BufferedSubset&gt;</c> (core-relations/src/pool/mod.rs:117-127, 424-446; 1 MiB per thread),
    /// which every <c>ColumnIndexShard</c> takes its map from (core-relations/src/hash_index/mod.rs:191-203, 490-501).
    /// </summary>
    /// <remarks>
    /// A table is kept whenever it has slots, as egglog keeps a map with any capacity, and weighs its slots plus their
    /// control bytes (egglog weighs an <c>IndexMap</c> as its capacity times a hash and a key-value pair).
    /// <see cref="RawHashTable{TEntry}.Clear"/> keeps the slots, so a table rented again holds as many values as it
    /// last grew to without rehashing. Clearing costs a pass over the control bytes; the entries hold no references and
    /// are not touched.
    /// </remarks>
    internal readonly struct ColumnIndexHashPolicy : IPoolPolicy<RawHashTable<ColumnIndexEntry>>
    {

        /// <inheritdoc />
        public static RawHashTable<ColumnIndexEntry> Create() => new();

        /// <inheritdoc />
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Clear(RawHashTable<ColumnIndexEntry> item) => item.Clear();

        /// <inheritdoc />
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool ShouldReuse(RawHashTable<ColumnIndexEntry> item) => item.SlotCount > 0;

        /// <inheritdoc />
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static long Bytes(RawHashTable<ColumnIndexEntry> item) => (long)item.SlotCount * (Unsafe.SizeOf<ColumnIndexEntry>() + 1);

        /// <inheritdoc />
        public static long LimitBytes => PoolLimits.ColumnIndex;

    }

}

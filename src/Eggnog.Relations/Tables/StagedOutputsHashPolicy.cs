using System.Runtime.CompilerServices;

namespace Eggnog.Relations
{

    /// <summary>
    /// Pool policy for the key index of <see cref="StagedOutputs"/>: egglog's <c>staged_outputs</c> pool of
    /// <c>HashTable&lt;TableEntry&gt;</c> (core-relations/src/pool/mod.rs, 32 MiB per thread). An index is kept whenever
    /// it has slots, and weighs its slots plus control bytes; <see cref="RawHashTable{TEntry}.Clear"/> keeps them.
    /// </summary>
    internal readonly struct StagedOutputsHashPolicy : IPoolPolicy<RawHashTable<TableEntry>>
    {

        /// <inheritdoc />
        public static RawHashTable<TableEntry> Create() => new();

        /// <inheritdoc />
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Clear(RawHashTable<TableEntry> item) => item.Clear();

        /// <inheritdoc />
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool ShouldReuse(RawHashTable<TableEntry> item) => item.SlotCount > 0;

        /// <inheritdoc />
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static long Bytes(RawHashTable<TableEntry> item) => (long)item.SlotCount * (Unsafe.SizeOf<TableEntry>() + 1);

        /// <inheritdoc />
        public static long LimitBytes => PoolLimits.StagedOutputs;

    }

}

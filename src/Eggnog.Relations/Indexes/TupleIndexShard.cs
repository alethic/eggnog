using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace Eggnog.Relations
{

    /// <summary>
    /// One shard of a <see cref="TupleIndex"/>: a SwissTable of <see cref="TupleIndexEntry"/>s, the buffer holding each
    /// distinct key once, and the arena of the sparse subsets. egglog's <c>TupleIndexShard</c> with its
    /// <c>SubsetTable</c> (core-relations/src/hash_index/mod.rs:123-144, 535-539).
    /// </summary>
    /// <remarks>
    /// <para>
    /// A key is stored once, as a row of <see cref="KeyData"/>, when it is first added; entries refer to it by row and
    /// cache its full hash, so a probe compares the hash and then, for a hash match, the key's cells in place.
    /// Iteration follows the hash table's slot order, as egglog's follows hashbrown's.
    /// </para>
    /// <para>
    /// Storage is pooled as egglog pools it. The hash table comes from the engine-wide pool of
    /// <see cref="TupleIndexHashPolicy"/> (egglog's <c>hash: Pooled&lt;HashTable&gt;</c> from the <c>tuple_indexes</c>
    /// pool) and the arena from <see cref="SubsetArenaPolicy"/>'s. The key buffer is a <see cref="RowBuffer"/>, whose
    /// storage comes from <see cref="System.Buffers.ArrayPool{T}.Shared"/> (the engine's counterpart of egglog's
    /// <c>vec_cell_vals</c> pool) and goes back on <see cref="Release"/>. egglog's key vector starts with the capacity a
    /// pooled vector last grew to; the shared array pool rents by length instead, so when the shard rents its table it
    /// reserves the key buffer for as many keys as the table holds without growing, which is the most distinct keys the
    /// table held before. A per-subset index built and disposed over and over (the join's <c>group_by_key</c>) then
    /// reuses its table, key storage and arena at their grown sizes and allocates no storage.
    /// </para>
    /// <para>
    /// egglog takes a table when the shard is created; here a shard holds the empty <see cref="Unallocated"/> table
    /// until its first key, so shards that never receive one rent nothing, and after <see cref="Release"/> it goes back
    /// to that state and stays usable.
    /// </para>
    /// <para>
    /// A mutable struct held in its index's shard array and mutated through <c>ref</c>; a parallel build works on a
    /// local copy and writes it back. Readers may run concurrently while nobody writes.
    /// </para>
    /// </remarks>
    internal struct TupleIndexShard
    {

        /// <summary>
        /// The table of every shard that holds no rented table: empty, with no slots, read but never written. Every
        /// write path swaps in a pooled table first (<see cref="WritableTable"/>).
        /// </summary>
        static readonly RawHashTable<TupleIndexEntry> Unallocated = new();

        RawHashTable<TupleIndexEntry> table;
        RowBuffer keys;
        SubsetBuffer subsets;

        /// <summary>
        /// Creates an empty shard for keys of <paramref name="arity"/> cells. Rents and allocates no storage until the
        /// first key.
        /// </summary>
        /// <param name="arity">The key arity; at least one.</param>
        /// <returns>The shard.</returns>
        public static TupleIndexShard Create(int arity)
        {
            var shard = default(TupleIndexShard);
            shard.table = Unallocated;
            shard.keys = new RowBuffer(arity);
            shard.subsets = SubsetBuffer.Create();
            return shard;
        }

        /// <summary>
        /// Gets the number of distinct keys.
        /// </summary>
        public readonly int Count
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => table.Count;
        }

        /// <summary>
        /// Gets the key storage: key row <c>r</c> occupies <c>[r * arity, (r + 1) * arity)</c>. Replaced when it grows.
        /// </summary>
        public readonly uint[] KeyData
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => keys.Data;
        }

        /// <summary>
        /// Gets the arena the sparse subsets index.
        /// </summary>
        public readonly int[] Arena
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => subsets.Rows;
        }

        /// <summary>
        /// Gets the hash table, for iteration. Do not modify it.
        /// </summary>
        public readonly RawHashTable<TupleIndexEntry> Table => table;

        /// <summary>
        /// Gets whether the shard holds a rented hash table, for diagnostics.
        /// </summary>
        internal readonly bool HasTable => ReferenceEquals(table, Unallocated) == false;

        /// <summary>
        /// Looks up the rows of the key <paramref name="match"/> recognizes, whose hash is <paramref name="hash"/>.
        /// O(1) expected.
        /// </summary>
        /// <typeparam name="TMatch">The match type.</typeparam>
        /// <param name="hash">The key's hash.</param>
        /// <param name="match">Recognizes the key.</param>
        /// <param name="rows">The rows, valid until the index is next modified.</param>
        /// <returns><c>true</c> if the key is present.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public readonly bool TryGetSubset<TMatch>(ulong hash, ref TMatch match, out SubsetRef rows)
            where TMatch : struct, IEntryMatch<TupleIndexEntry>
        {
            var slot = table.Find(hash, ref match);
            if (slot < 0)
            {
                rows = default;
                return false;
            }

            rows = table.EntryAt(slot).Subset.AsRef(subsets.Rows);
            return true;
        }

        /// <summary>
        /// Appends <paramref name="row"/> to the rows of the key <paramref name="match"/> recognizes, storing the key
        /// (<paramref name="key"/>) if it is new: egglog's <c>TupleIndex::add_row</c>
        /// (core-relations/src/hash_index/mod.rs:590-620). Amortized O(1) plus the key compare.
        /// </summary>
        /// <typeparam name="TMatch">The match type; built over the current <see cref="KeyData"/>. Renting a table can
        /// replace <see cref="KeyData"/>, but a freshly rented table is empty, so the match is not consulted then.</typeparam>
        /// <param name="hash">The key's hash.</param>
        /// <param name="match">Recognizes the key.</param>
        /// <param name="key">The key's cells.</param>
        /// <param name="row">The row id; at least every row already listed under the key.</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void AddRow<TMatch>(ulong hash, ref TMatch match, ReadOnlySpan<uint> key, int row)
            where TMatch : struct, IEntryMatch<TupleIndexEntry>
        {
            var map = WritableTable();
            var slot = map.FindOrFindInsertSlot(hash, ref match, default(TupleIndexEntryHasher));
            if (slot >= 0)
            {
                ref var entry = ref map.EntryAt(slot);
                entry = entry.WithSubset(entry.Subset.AddRowSorted(row, ref subsets));
                return;
            }

            var keyRow = keys.AddRow(key);
            map.InsertInSlot(hash, ~slot, new TupleIndexEntry(hash, keyRow, BufferedSubset.Singleton(row)));
        }

        /// <summary>
        /// Appends <paramref name="row"/> to the rows of the key held at <paramref name="key"/>[<paramref name="offset"/>],
        /// whose hash is <paramref name="hash"/>; the array form used by the parallel build.
        /// </summary>
        /// <param name="hash">The key's hash.</param>
        /// <param name="key">The array holding the key.</param>
        /// <param name="offset">The index of the key's first cell.</param>
        /// <param name="row">The row id.</param>
        public void AddRow(ulong hash, uint[] key, int offset, int row)
        {
            var arity = keys.Arity;
            var match = new TupleKeyMatch(keys.Data, arity, hash, key, offset);
            AddRow(hash, ref match, new ReadOnlySpan<uint>(key, offset, arity), row);
        }

        /// <summary>
        /// Gets the cells of key row <paramref name="keyRow"/>.
        /// </summary>
        /// <param name="keyRow">A key row named by an entry.</param>
        /// <returns>The key.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public readonly ReadOnlySpan<uint> GetKey(int keyRow) => keys.GetRow(keyRow);

        /// <summary>
        /// Makes room for <paramref name="additionalKeys"/> more keys without growing.
        /// </summary>
        /// <param name="additionalKeys">The number of keys about to be added.</param>
        public void Reserve(int additionalKeys)
        {
            if (additionalKeys <= 0)
                return;

            WritableTable().Reserve(additionalKeys, default(TupleIndexEntryHasher));
            keys.Reserve(additionalKeys);
        }

        /// <summary>
        /// Removes every key, keeping the storage (egglog keeps the pooled table too). egglog's
        /// <c>TupleIndex::clear</c> per shard.
        /// </summary>
        public void Clear()
        {
            if (HasTable)
                table.Clear();

            keys.Clear();
            subsets.Reset();
        }

        /// <summary>
        /// Creates an independent copy. O(keys + arena). The copy's storage is freshly allocated (a cold path: catalog
        /// clones) and goes to the pools when the copy is released.
        /// </summary>
        /// <returns>The copy.</returns>
        public readonly TupleIndexShard Clone()
        {
            var copy = default(TupleIndexShard);
            copy.table = HasTable ? table.Clone() : Unallocated;
            copy.keys = keys.Clone();
            copy.subsets = subsets.Clone();
            return copy;
        }

        /// <summary>
        /// Hands the hash table, the key storage and the arena back to their pools and empties the shard, which stays
        /// usable: egglog's drop of the shard's pooled table, key vector and arena. Allocates nothing. Releasing twice is
        /// harmless; subsets and key data taken before must not be used afterwards.
        /// </summary>
        public void Release()
        {
            var map = table;
            table = Unallocated;
            if (ReferenceEquals(map, Unallocated) == false)
                SharedPool<RawHashTable<TupleIndexEntry>, TupleIndexHashPolicy>.Return(map);

            keys.Dispose();
            subsets.Release();
        }

        /// <summary>
        /// Gets the table to write to, renting one first if the shard holds none. One reference compare on the hot path.
        /// </summary>
        /// <returns>The shard's own table.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        RawHashTable<TupleIndexEntry> WritableTable()
        {
            var map = table;
            if (ReferenceEquals(map, Unallocated))
                map = RentTable();

            return map;
        }

        /// <summary>
        /// Takes a cleared table from the calling thread's pool (egglog's <c>ps.get()</c>), with the slots it last grew
        /// to, or a new empty one, and reserves the key buffer for as many keys as the table holds without growing.
        /// </summary>
        /// <returns>The rented table, now the shard's.</returns>
        [MethodImpl(MethodImplOptions.NoInlining)]
        RawHashTable<TupleIndexEntry> RentTable()
        {
            var rented = SharedPool<RawHashTable<TupleIndexEntry>, TupleIndexHashPolicy>.Rent();
            Debug.Assert(rented.Count == 0, "a pooled table was not cleared");
            table = rented;

            // The key buffer holds no storage here: it only fills while a table is rented, and Release empties both.
            Debug.Assert(keys.Count == 0);
            if (keys.Capacity == 0 && rented.Capacity > 0)
                keys.Reserve(rented.Capacity);

            return rented;
        }

    }

}

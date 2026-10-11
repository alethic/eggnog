using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace Eggnog.Relations
{

    /// <summary>
    /// One shard of a <see cref="ColumnIndex"/>: a SwissTable from value to <see cref="BufferedSubset"/>, and the arena
    /// its sparse subsets live in. egglog's <c>ColumnIndexShard</c> (core-relations/src/hash_index/mod.rs:191-203).
    /// </summary>
    /// <remarks>
    /// <para>
    /// egglog's map is an <c>IndexMap</c>: an index table of positions into an insertion-ordered entries vector. Here
    /// the entries (<see cref="ColumnIndexEntry"/>, value and subset) sit in the hash table's slots, so a probe or an
    /// append costs one group load and one slot read, with no second dependent load into an entries array; iteration
    /// follows slot order, as egglog's <c>TupleIndex</c> iteration follows hashbrown's. Entries are never removed, only
    /// cleared all at once.
    /// </para>
    /// <para>
    /// Storage is pooled as egglog pools it: the hash table comes from the engine-wide pool of
    /// <see cref="ColumnIndexHashPolicy"/> (egglog's <c>table: Pooled&lt;IndexMap&gt;</c> from the <c>column_index</c>
    /// pool) and the arena from <see cref="SubsetArenaPolicy"/>'s, and <see cref="Release"/> hands both back, so a
    /// per-subset index that is built and disposed over and over (the join's <c>group_by_col</c>) reuses the same
    /// storage at its grown size. egglog takes a map when the shard is created; here a shard holds the empty
    /// <see cref="Unallocated"/> table until its first value, so shards that never receive one rent nothing, and after
    /// <see cref="Release"/> it goes back to that state and stays usable.
    /// </para>
    /// <para>
    /// A mutable struct held in its index's shard array and mutated through <c>ref</c>; a parallel build works on a
    /// local copy and writes it back. Readers may run concurrently while nobody writes.
    /// </para>
    /// </remarks>
    internal struct ColumnIndexShard
    {

        /// <summary>
        /// The table of every shard that holds no rented table: empty, with no slots, read but never written. Every
        /// write path swaps in a pooled table first (<see cref="WritableTable"/>).
        /// </summary>
        static readonly RawHashTable<ColumnIndexEntry> Unallocated = new();

        RawHashTable<ColumnIndexEntry> table;
        SubsetBuffer subsets;

        /// <summary>
        /// Creates an empty shard that rents and allocates nothing until the first value.
        /// </summary>
        /// <returns>The shard.</returns>
        public static ColumnIndexShard Create()
        {
            var shard = default(ColumnIndexShard);
            shard.table = Unallocated;
            shard.subsets = SubsetBuffer.Create();
            return shard;
        }

        /// <summary>
        /// Gets the number of values.
        /// </summary>
        public readonly int Count
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => table.Count;
        }

        /// <summary>
        /// Gets the hash table, for iteration. Do not modify it.
        /// </summary>
        public readonly RawHashTable<ColumnIndexEntry> Table => table;

        /// <summary>
        /// Gets the arena the sparse subsets index.
        /// </summary>
        public readonly int[] Arena
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => subsets.Rows;
        }

        /// <summary>
        /// Gets the number of arena slots in use, for diagnostics.
        /// </summary>
        public readonly int ArenaLength => subsets.Length;

        /// <summary>
        /// Gets whether the shard holds a rented hash table, for diagnostics.
        /// </summary>
        internal readonly bool HasTable => ReferenceEquals(table, Unallocated) == false;

        /// <summary>
        /// Looks up the rows of <paramref name="key"/>, whose Fx hash is <paramref name="hash"/>. O(1) expected.
        /// </summary>
        /// <param name="key">The value.</param>
        /// <param name="hash">The value's hash.</param>
        /// <param name="rows">The rows, valid until the index is next modified.</param>
        /// <returns><c>true</c> if the value is present (its subset is then nonempty).</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public readonly bool TryGetSubset(uint key, ulong hash, out SubsetRef rows)
        {
            var match = new ColumnIndexKeyMatch(key);
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
        /// Appends <paramref name="row"/> to the rows of <paramref name="key"/>, adding the value if it is new: egglog's
        /// <c>entry(key).or_insert_with(empty).add_row_sorted(row)</c>. <paramref name="row"/> must be at least every row
        /// already listed under the value. Amortized O(1).
        /// </summary>
        /// <param name="key">The value.</param>
        /// <param name="hash">The value's Fx hash.</param>
        /// <param name="row">The row id.</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void AddRow(uint key, ulong hash, int row)
        {
            var map = WritableTable();
            var match = new ColumnIndexKeyMatch(key);
            var slot = map.FindOrFindInsertSlot(hash, ref match, default(ColumnIndexEntryHasher));
            if (slot >= 0)
            {
                ref var entry = ref map.EntryAt(slot);
                entry = new ColumnIndexEntry(key, entry.Subset.AddRowSorted(row, ref subsets));
                return;
            }

            map.InsertInSlot(hash, ~slot, new ColumnIndexEntry(key, BufferedSubset.Singleton(row)));
        }

        /// <summary>
        /// Adds a value the caller knows is absent, with its whole subset: the insert of egglog's sort-based
        /// <c>rebuild_full</c>. O(1) expected.
        /// </summary>
        /// <param name="key">The value.</param>
        /// <param name="hash">The value's Fx hash.</param>
        /// <param name="subset">The value's rows; a sparse subset must come from this shard's arena.</param>
        public void InsertNew(uint key, ulong hash, BufferedSubset subset)
        {
            Debug.Assert(TryGetSubset(key, hash, out _) == false, "the value is already present");
            WritableTable().InsertUnique(hash, new ColumnIndexEntry(key, subset), default(ColumnIndexEntryHasher));
        }

        /// <summary>
        /// Allocates an arena block for a run of <paramref name="count"/> row ids; the caller writes them into
        /// <see cref="Arena"/> at the returned position (re-reading <see cref="Arena"/>) and passes
        /// <see cref="BufferedSubset.Sparse"/> of the run to <see cref="InsertNew"/>.
        /// </summary>
        /// <param name="count">The run length; at least one.</param>
        /// <returns>The block's first position.</returns>
        public int AllocateRun(int count) => subsets.Allocate(count);

        /// <summary>
        /// Makes room for <paramref name="additionalKeys"/> more values and <paramref name="arenaSlots"/> more arena
        /// slots without growing.
        /// </summary>
        /// <param name="additionalKeys">The number of values about to be added.</param>
        /// <param name="arenaSlots">The number of arena slots about to be allocated.</param>
        public void Reserve(int additionalKeys, long arenaSlots)
        {
            if (additionalKeys > 0)
                WritableTable().Reserve(additionalKeys, default(ColumnIndexEntryHasher));

            if (arenaSlots > 0)
                subsets.Reserve(arenaSlots);
        }

        /// <summary>
        /// Removes every value, keeping the storage (egglog keeps the pooled map too). O(slots) for the hash table, O(1)
        /// for the arena.
        /// </summary>
        public void Clear()
        {
            if (HasTable)
                table.Clear();

            subsets.Reset();
        }

        /// <summary>
        /// Creates an independent copy. O(slots + arena). The copy's storage is freshly allocated (a cold path: catalog
        /// clones) and goes to the pools when the copy is released.
        /// </summary>
        /// <returns>The copy.</returns>
        public readonly ColumnIndexShard Clone()
        {
            var copy = default(ColumnIndexShard);
            copy.table = HasTable ? table.Clone() : Unallocated;
            copy.subsets = subsets.Clone();
            return copy;
        }

        /// <summary>
        /// Hands the hash table and the arena back to their pools and empties the shard, which stays usable: egglog's
        /// drop of the shard's pooled map and arena. Allocates nothing. Releasing twice is harmless; subsets taken before
        /// must not be used afterwards.
        /// </summary>
        public void Release()
        {
            var map = table;
            table = Unallocated;
            if (ReferenceEquals(map, Unallocated) == false)
                SharedPool<RawHashTable<ColumnIndexEntry>, ColumnIndexHashPolicy>.Return(map);

            subsets.Release();
        }

        /// <summary>
        /// Gets the table to write to, renting one first if the shard holds none. One reference compare on the hot path.
        /// </summary>
        /// <returns>The shard's own table.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        RawHashTable<ColumnIndexEntry> WritableTable()
        {
            var map = table;
            if (ReferenceEquals(map, Unallocated))
                map = RentTable();

            return map;
        }

        /// <summary>
        /// Takes a cleared table from the calling thread's pool (egglog's <c>ps.get()</c>), with the slots it last grew
        /// to, or a new empty one.
        /// </summary>
        /// <returns>The rented table, now the shard's.</returns>
        [MethodImpl(MethodImplOptions.NoInlining)]
        RawHashTable<ColumnIndexEntry> RentTable()
        {
            var rented = SharedPool<RawHashTable<ColumnIndexEntry>, ColumnIndexHashPolicy>.Rent();
            Debug.Assert(rented.Count == 0, "a pooled table was not cleared");
            table = rented;
            return rented;
        }

    }

}

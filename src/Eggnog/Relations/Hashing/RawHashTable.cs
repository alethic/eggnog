using System;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Eggnog.Relations
{

    /// <summary>
    /// An open-addressing SwissTable of <typeparamref name="TEntry"/> structs: the port of hashbrown's
    /// <c>HashTable&lt;T&gt;</c>, which egglog uses for every key-to-row index (table entries, tuple indexes, staged
    /// outputs, intern tables).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The table stores entries, not keys and values: the caller supplies each entry's 64-bit hash, an
    /// <see cref="IEntryMatch{TEntry}"/> to recognize the entry a lookup wants, and an
    /// <see cref="IEntryHasher{TEntry}"/> to recompute hashes when the table grows. All three are struct type
    /// parameters, so every probe loop is specialized and the comparison inlined; there are no delegates or interface
    /// calls on any path.
    /// </para>
    /// <para>
    /// Layout: <c>2^k</c> slots, a control byte per slot plus a trailing copy of the first
    /// <see cref="ControlGroup.Width"/> control bytes, so that a 16-byte group load at any slot never wraps. A probe
    /// starts at <c>hash &amp; mask</c>, compares the group's control bytes against the hash's 7-bit tag with one SIMD
    /// compare, checks the candidates, and stops at the first group containing an empty byte; otherwise it moves on
    /// by a triangular stride (16, 32, 48, ... slots), which visits every group once. A removal leaves a tombstone only
    /// when the slot sits in a run of at least 16 non-empty slots that a probe might have walked through.
    /// </para>
    /// <para>
    /// Load factor 7/8 (tables of at most 8 slots keep one slot empty). When an insert finds no growth left, the
    /// table rehashes in place if at most half its capacity is live (reclaiming tombstones), and otherwise doubles.
    /// Expected cost of a lookup or insert: O(1), typically one group load and one entry comparison.
    /// </para>
    /// <para>
    /// Not thread-safe. Any number of threads may look up concurrently while nobody mutates it.
    /// </para>
    /// </remarks>
    /// <typeparam name="TEntry">The entry type.</typeparam>
    internal sealed class RawHashTable<TEntry>
        where TEntry : struct
    {

        /// <summary>
        /// The largest slot count: entry arrays stay below the CLR's element limit.
        /// </summary>
        const int MaxBuckets = 1 << 30;

        byte[] ctrl;
        TEntry[] entries;
        int bucketMask;
        int growthLeft;
        int items;

        /// <summary>
        /// Initializes a new, empty instance that allocates nothing until the first insert.
        /// </summary>
        public RawHashTable()
        {
            ctrl = ControlGroup.EmptySingleton;
            entries = Array.Empty<TEntry>();
        }

        /// <summary>
        /// Initializes a new instance that holds at least <paramref name="capacity"/> entries without growing.
        /// </summary>
        /// <param name="capacity">The number of entries to make room for.</param>
        public RawHashTable(int capacity)
            : this()
        {
            ArgumentOutOfRangeException.ThrowIfNegative(capacity);

            if (capacity > 0)
            {
                var buckets = CapacityToBuckets(capacity);
                ctrl = AllocateControl(buckets);
                entries = GC.AllocateUninitializedArray<TEntry>(buckets);
                bucketMask = buckets - 1;
                growthLeft = BucketMaskToCapacity(bucketMask);
            }
        }

        /// <summary>
        /// Gets the number of entries.
        /// </summary>
        public int Count => items;

        /// <summary>
        /// Gets whether the table has no entries.
        /// </summary>
        public bool IsEmpty => items == 0;

        /// <summary>
        /// Gets the number of entries the table holds before it must grow or rehash: the entries present plus the
        /// remaining growth (tombstones consume growth until a rehash reclaims them). hashbrown's <c>capacity()</c>.
        /// </summary>
        public int Capacity => items + growthLeft;

        /// <summary>
        /// Gets the number of slots: zero before the first allocation, otherwise a power of two. Valid slot indices
        /// are <c>[0, SlotCount)</c>.
        /// </summary>
        public int SlotCount => entries.Length;

        /// <summary>
        /// Gets the control bytes, including the trailing mirror group. For diagnostics and tests.
        /// </summary>
        internal ReadOnlySpan<byte> Control => ctrl;

        /// <summary>
        /// Gets the slot array, full or not. For diagnostics and tests.
        /// </summary>
        internal ReadOnlySpan<TEntry> RawEntries => entries;

        /// <summary>
        /// Gets the remaining growth: how many inserts into empty slots may happen before the next resize or rehash.
        /// </summary>
        internal int GrowthLeft => growthLeft;

        /// <summary>
        /// Finds the slot of the entry <paramref name="match"/> accepts among the entries inserted with
        /// <paramref name="hash"/>. hashbrown's <c>find</c>.
        /// </summary>
        /// <typeparam name="TMatch">The match type.</typeparam>
        /// <param name="hash">The 64-bit hash of what is looked up.</param>
        /// <param name="match">Recognizes the entry.</param>
        /// <returns>The slot, or <c>-1</c> if no entry matches.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int Find<TMatch>(ulong hash, ref TMatch match)
            where TMatch : struct, IEntryMatch<TEntry>
        {
            // Both arrays are read once: a probe sees one consistent generation of the table. The empty singleton is a
            // single EMPTY group, so a probe of an unallocated table stops after one load without touching entries.
            var mask = bucketMask;
            ref var control = ref MemoryMarshal.GetArrayDataReference(ctrl);
            ref var slots = ref MemoryMarshal.GetArrayDataReference(entries);
            var tag = ControlGroup.Tag(hash);
            var pos = (int)hash & mask;
            var stride = 0;

            while (true)
            {
                var group = ControlGroup.Load(ref control, pos);

                for (var bits = ControlGroup.MatchTag(group, tag); bits != 0; bits = ControlGroup.RemoveLowestBit(bits))
                {
                    // SAFETY: (pos + bit) & mask < SlotCount whenever a tag matched, because only full slots carry tags
                    // and the empty singleton has none.
                    var index = (pos + ControlGroup.LowestSetBit(bits)) & mask;
                    if (match.Matches(in Unsafe.Add(ref slots, (nint)(uint)index)))
                        return index;
                }

                if (ControlGroup.MatchEmpty(group) != 0)
                    return -1;

                // hashbrown's ProbeSeq::move_next. Every table keeps an EMPTY slot, and the triangular sequence visits
                // every group within SlotCount / Width moves, so a probe that has not stopped by then means the
                // control bytes are corrupt.
                Debug.Assert(stride <= mask, "Went past end of probe sequence");
                stride += ControlGroup.Width;
                pos = (pos + stride) & mask;
            }
        }

        /// <summary>
        /// Finds the entry <paramref name="match"/> accepts and returns a reference to it. hashbrown's
        /// <c>find_mut</c>. The reference is valid until the table is next modified.
        /// </summary>
        /// <typeparam name="TMatch">The match type.</typeparam>
        /// <param name="hash">The 64-bit hash of what is looked up.</param>
        /// <param name="match">Recognizes the entry.</param>
        /// <param name="found">Whether an entry matched.</param>
        /// <returns>A reference to the entry, or a null reference when <paramref name="found"/> is
        /// <c>false</c>.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ref TEntry FindRef<TMatch>(ulong hash, ref TMatch match, out bool found)
            where TMatch : struct, IEntryMatch<TEntry>
        {
            var index = Find(hash, ref match);
            if (index < 0)
            {
                found = false;
                return ref Unsafe.NullRef<TEntry>();
            }

            found = true;
            return ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(entries), (nint)(uint)index);
        }

        /// <summary>
        /// Finds the entry <paramref name="match"/> accepts and copies it out.
        /// </summary>
        /// <typeparam name="TMatch">The match type.</typeparam>
        /// <param name="hash">The 64-bit hash of what is looked up.</param>
        /// <param name="match">Recognizes the entry.</param>
        /// <param name="entry">The entry, or <c>default</c> if none matched.</param>
        /// <returns><c>true</c> if an entry matched.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool TryFind<TMatch>(ulong hash, ref TMatch match, out TEntry entry)
            where TMatch : struct, IEntryMatch<TEntry>
        {
            var index = Find(hash, ref match);
            if (index < 0)
            {
                entry = default;
                return false;
            }

            entry = Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(entries), (nint)(uint)index);
            return true;
        }

        /// <summary>
        /// Inserts an entry the caller knows is not present, growing first if needed. hashbrown's
        /// <c>insert_unique</c>: no lookup is made, so inserting a duplicate leaves two entries.
        /// </summary>
        /// <typeparam name="THasher">The hasher type.</typeparam>
        /// <param name="hash">The entry's 64-bit hash.</param>
        /// <param name="entry">The entry.</param>
        /// <param name="hasher">Recomputes the hashes of stored entries if the table grows.</param>
        /// <returns>The slot the entry went to; valid until the table is next modified.</returns>
        public int InsertUnique<THasher>(ulong hash, in TEntry entry, THasher hasher)
            where THasher : struct, IEntryHasher<TEntry>
        {
            var index = FindInsertSlot(ctrl, bucketMask, hash);

            // Replacing a tombstone does not use up an EMPTY slot, so it needs no growth even at the load limit.
            // growthLeft never goes negative; testing <= 0 instead of hashbrown's == 0 costs nothing and keeps a
            // corrupted count from disabling growth.
            var old = ctrl[index];
            if (growthLeft <= 0 && old == ControlGroup.Empty)
            {
                ReserveRehash(1, hasher);
                index = FindInsertSlot(ctrl, bucketMask, hash);
                old = ctrl[index];
            }

            RecordItemInsertAt(index, old, hash);
            entries[index] = entry;
            return index;
        }

        /// <summary>
        /// Looks up an entry and, if it is absent, finds the slot to insert it into: the lookup half of hashbrown's
        /// <c>HashTable::entry</c>. Like hashbrown, it first makes room for one more entry, so the table may grow
        /// even when the entry is found.
        /// </summary>
        /// <remarks>
        /// When the result is negative, fill the slot <c>~result</c> with <see cref="InsertInSlot"/> before any other
        /// change to the table (hashbrown's <c>VacantEntry::insert</c>).
        /// </remarks>
        /// <typeparam name="TMatch">The match type.</typeparam>
        /// <typeparam name="THasher">The hasher type.</typeparam>
        /// <param name="hash">The 64-bit hash of what is looked up.</param>
        /// <param name="match">Recognizes the entry.</param>
        /// <param name="hasher">Recomputes the hashes of stored entries if the table grows.</param>
        /// <returns>The slot of the matching entry, or the bitwise complement of the slot to insert into.</returns>
        public int FindOrFindInsertSlot<TMatch, THasher>(ulong hash, ref TMatch match, THasher hasher)
            where TMatch : struct, IEntryMatch<TEntry>
            where THasher : struct, IEntryHasher<TEntry>
        {
            if (growthLeft < 1)
                ReserveRehash(1, hasher);

            var mask = bucketMask;
            ref var control = ref MemoryMarshal.GetArrayDataReference(ctrl);
            ref var slots = ref MemoryMarshal.GetArrayDataReference(entries);
            var tag = ControlGroup.Tag(hash);
            var pos = (int)hash & mask;
            var stride = 0;
            var insertSlot = -1;

            while (true)
            {
                var group = ControlGroup.Load(ref control, pos);

                for (var bits = ControlGroup.MatchTag(group, tag); bits != 0; bits = ControlGroup.RemoveLowestBit(bits))
                {
                    var index = (pos + ControlGroup.LowestSetBit(bits)) & mask;
                    if (match.Matches(in Unsafe.Add(ref slots, (nint)(uint)index)))
                        return index;
                }

                // Remember the first EMPTY or DELETED slot of the probe, but keep looking until a group with an EMPTY
                // byte proves the entry absent: it may sit past a tombstone.
                if (insertSlot < 0)
                {
                    var special = ControlGroup.MatchEmptyOrDeleted(group);
                    if (special != 0)
                        insertSlot = (pos + ControlGroup.LowestSetBit(special)) & mask;
                }

                if (insertSlot >= 0 && ControlGroup.MatchEmpty(group) != 0)
                    return ~FixInsertSlot(ctrl, insertSlot);

                Debug.Assert(stride <= mask, "Went past end of probe sequence");
                stride += ControlGroup.Width;
                pos = (pos + stride) & mask;
            }
        }

        /// <summary>
        /// Fills a slot that <see cref="FindOrFindInsertSlot{TMatch, THasher}"/> returned (complemented) for an absent
        /// entry. hashbrown's <c>VacantEntry::insert</c>.
        /// </summary>
        /// <param name="hash">The entry's 64-bit hash: the one passed to the lookup.</param>
        /// <param name="slot">The insert slot, <c>~result</c> of the lookup.</param>
        /// <param name="entry">The entry.</param>
        public void InsertInSlot(ulong hash, int slot, in TEntry entry)
        {
            if ((uint)slot >= (uint)entries.Length || ControlGroup.IsFull(ctrl[slot]))
                throw new ArgumentOutOfRangeException(nameof(slot), "The slot is not a vacant slot of this table.");

            // hashbrown's VacantEntry can only hold a slot found right after reserve(1), so an EMPTY slot always has
            // growth to use up. The two-call API cannot enforce that, and filling an EMPTY slot with no growth left
            // would drive growthLeft negative and eventually leave no EMPTY byte to stop a probe, so check it.
            var old = ctrl[slot];
            if (old == ControlGroup.Empty && growthLeft <= 0)
                throw new InvalidOperationException("The slot was not obtained from FindOrFindInsertSlot, or the table changed since.");

            RecordItemInsertAt(slot, old, hash);
            entries[slot] = entry;
        }

        /// <summary>
        /// Removes the entry <paramref name="match"/> accepts, if any. hashbrown's <c>find_entry(..).remove()</c>.
        /// </summary>
        /// <typeparam name="TMatch">The match type.</typeparam>
        /// <param name="hash">The 64-bit hash of what is removed.</param>
        /// <param name="match">Recognizes the entry.</param>
        /// <param name="removed">The removed entry, or <c>default</c>.</param>
        /// <returns><c>true</c> if an entry was removed.</returns>
        public bool Remove<TMatch>(ulong hash, ref TMatch match, out TEntry removed)
            where TMatch : struct, IEntryMatch<TEntry>
        {
            var index = Find(hash, ref match);
            if (index < 0)
            {
                removed = default;
                return false;
            }

            removed = entries[index];
            Erase(index);
            return true;
        }

        /// <summary>
        /// Removes the entry in a full slot. hashbrown's <c>OccupiedEntry::remove</c>.
        /// </summary>
        /// <param name="slot">A full slot.</param>
        public void RemoveAt(int slot)
        {
            if ((uint)slot >= (uint)entries.Length || ControlGroup.IsFull(ctrl[slot]) == false)
                throw new ArgumentOutOfRangeException(nameof(slot), "The slot is not a full slot of this table.");

            Erase(slot);
        }

        /// <summary>
        /// Removes every entry <paramref name="predicate"/> rejects, in one pass over the slots. hashbrown's
        /// <c>retain</c>. O(<see cref="SlotCount"/>) at most: the pass stops after the last entry, so it is O(1) on an
        /// empty table.
        /// </summary>
        /// <typeparam name="TPredicate">The predicate type.</typeparam>
        /// <param name="predicate">Decides which entries stay; may update them in place.</param>
        public void Retain<TPredicate>(ref TPredicate predicate)
            where TPredicate : struct, IEntryPredicate<TEntry>
        {
            var slots = entries;
            ref var control = ref MemoryMarshal.GetArrayDataReference(ctrl);

            // Like hashbrown's RawIter, count down the entries present at the start (erasures do not change how many
            // lie ahead) and stop at zero: an empty table costs nothing and a sparse one stops at its last entry.
            // Erasing writes only the erased slot's control byte (and its mirror, which lies past the aligned groups
            // scanned here), so each group's full mask stays valid while its entries are visited.
            var remaining = items;
            for (var groupBase = 0; remaining > 0 && groupBase < slots.Length; groupBase += ControlGroup.Width)
            {
                for (var bits = ControlGroup.MatchFull(ControlGroup.Load(ref control, groupBase)); bits != 0; bits = ControlGroup.RemoveLowestBit(bits))
                {
                    var index = groupBase + ControlGroup.LowestSetBit(bits);
                    remaining--;
                    if (predicate.Keep(ref slots[index]) == false)
                        Erase(index);
                }
            }
        }

        /// <summary>
        /// Removes every entry, keeping the allocation. O(<see cref="SlotCount"/>) unless the table is already empty
        /// with no tombstones.
        /// </summary>
        public void Clear()
        {
            // hashbrown returns early whenever the table has no items; this also resets tombstones left by removals,
            // which would otherwise lengthen probes until the next rehash. The empty singleton always returns here.
            if (items == 0 && growthLeft == BucketMaskToCapacity(bucketMask))
                return;

            ctrl.AsSpan().Fill(ControlGroup.Empty);
            if (RuntimeHelpers.IsReferenceOrContainsReferences<TEntry>())
                Array.Clear(entries);

            items = 0;
            growthLeft = BucketMaskToCapacity(bucketMask);
        }

        /// <summary>
        /// Makes room for <paramref name="additional"/> more entries without further growth. hashbrown's
        /// <c>reserve</c>.
        /// </summary>
        /// <typeparam name="THasher">The hasher type.</typeparam>
        /// <param name="additional">The number of entries about to be inserted.</param>
        /// <param name="hasher">Recomputes the hashes of stored entries.</param>
        public void Reserve<THasher>(int additional, THasher hasher)
            where THasher : struct, IEntryHasher<TEntry>
        {
            ArgumentOutOfRangeException.ThrowIfNegative(additional);

            if (additional > growthLeft)
                ReserveRehash(additional, hasher);
        }

        /// <summary>
        /// Gets whether a slot holds an entry.
        /// </summary>
        /// <param name="slot">The slot, in <c>[0, SlotCount)</c>.</param>
        /// <returns><c>true</c> if full.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool IsFull(int slot)
        {
            if ((uint)slot >= (uint)entries.Length)
                throw new ArgumentOutOfRangeException(nameof(slot));

            return ControlGroup.IsFull(ctrl[slot]);
        }

        /// <summary>
        /// Gets a reference to the entry in a slot, which the caller knows is full (from <see cref="Find"/>,
        /// <see cref="NextFullSlot"/> or enumeration). Valid until the table is next modified.
        /// </summary>
        /// <param name="slot">A full slot.</param>
        /// <returns>A reference to the entry.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ref TEntry EntryAt(int slot) => ref entries[slot];

        /// <summary>
        /// Gets a copy of the entry in a slot, which the caller knows is full. The non-<c>ref</c> form of
        /// <see cref="EntryAt"/>, for callers that keep a copy (the indexes' <c>NextEntry</c>, the intern table's
        /// lookups, the container walks).
        /// </summary>
        /// <param name="slot">A full slot.</param>
        /// <returns>The entry.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public TEntry GetEntry(int slot) => entries[slot];

        /// <summary>
        /// Gets the first full slot at or after <paramref name="slot"/>, scanning a group at a time: an external
        /// walk by slot, for callers that need the slot (the container walks) or resume the walk across calls (the
        /// indexes' <c>NextEntry</c>) instead of holding the struct enumerator. Slots come in ascending order.
        /// </summary>
        /// <param name="slot">The slot to start from, at least zero.</param>
        /// <returns>The slot, or <c>-1</c> if there is none.</returns>
        public int NextFullSlot(int slot)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(slot);

            // An empty table, however large, has no full slot to scan for.
            if (items == 0)
                return -1;

            var buckets = entries.Length;
            ref var control = ref MemoryMarshal.GetArrayDataReference(ctrl);

            // An unaligned load at pos < buckets stays inside the array thanks to the mirror group. Its bytes past the
            // last slot are mirrors (or EMPTY in tables smaller than a group): if the first full byte lies there, no
            // full slot remains.
            for (var pos = slot; pos < buckets; pos += ControlGroup.Width)
            {
                var bits = ControlGroup.MatchFull(ControlGroup.Load(ref control, pos));
                if (bits != 0)
                {
                    var next = pos + ControlGroup.LowestSetBit(bits);
                    return next < buckets ? next : -1;
                }
            }

            return -1;
        }

        /// <summary>
        /// Creates a copy with the same slots, control bytes and capacity. O(<see cref="SlotCount"/>).
        /// </summary>
        /// <returns>The copy.</returns>
        public RawHashTable<TEntry> Clone()
        {
            var copy = new RawHashTable<TEntry>();
            if (entries.Length != 0)
            {
                copy.ctrl = (byte[])ctrl.Clone();
                copy.entries = (TEntry[])entries.Clone();
            }

            copy.bucketMask = bucketMask;
            copy.growthLeft = growthLeft;
            copy.items = items;
            return copy;
        }

        /// <summary>
        /// Gets an enumerator over the entries in ascending slot order, yielding references. The table must not be
        /// modified during enumeration, except by writing through the yielded references.
        /// </summary>
        /// <returns>The enumerator.</returns>
        public RawHashTableEnumerator<TEntry> GetEnumerator() => new(ctrl, entries, items);

        /// <summary>
        /// Gets the number of entries a table with <c>bucketMask + 1</c> slots holds: 7/8 of the slots, or all but one
        /// for tables of at most 8 slots. hashbrown's <c>bucket_mask_to_capacity</c>.
        /// </summary>
        /// <param name="bucketMask">The slot count minus one.</param>
        /// <returns>The capacity.</returns>
        internal static int BucketMaskToCapacity(int bucketMask) => bucketMask < 8 ? bucketMask : ((bucketMask + 1) / 8) * 7;

        /// <summary>
        /// Gets the slot count for a requested capacity. hashbrown's <c>capacity_to_buckets</c> with the
        /// small-table minimum it applies to entries of four bytes or more: tables start at 4 slots (3 entries), then
        /// 8 (7) and 16 (14); larger ones round <c>capacity * 8 / 7</c> up to a power of two.
        /// </summary>
        /// <param name="capacity">The capacity, at least one.</param>
        /// <returns>The slot count.</returns>
        internal static int CapacityToBuckets(int capacity)
        {
            Debug.Assert(capacity > 0);

            if (capacity < 15)
                return capacity < 4 ? 4 : capacity < 8 ? 8 : 16;

            if (capacity > BucketMaskToCapacity(MaxBuckets - 1))
                throw new InvalidOperationException("Hash table capacity overflow.");

            var adjusted = (int)((long)capacity * 8 / 7);
            return (int)BitOperations.RoundUpToPowerOf2((uint)adjusted);
        }

        /// <summary>
        /// Finds the first EMPTY or DELETED slot on the probe sequence of <paramref name="hash"/>. hashbrown's
        /// <c>find_insert_slot</c>. The table must have at least one EMPTY slot.
        /// </summary>
        static int FindInsertSlot(byte[] ctrl, int mask, ulong hash)
        {
            ref var control = ref MemoryMarshal.GetArrayDataReference(ctrl);
            var pos = (int)hash & mask;
            var stride = 0;

            while (true)
            {
                var bits = ControlGroup.MatchEmptyOrDeleted(ControlGroup.Load(ref control, pos));
                if (bits != 0)
                    return FixInsertSlot(ctrl, (pos + ControlGroup.LowestSetBit(bits)) & mask);

                Debug.Assert(stride <= mask, "Went past end of probe sequence");
                stride += ControlGroup.Width;
                pos = (pos + stride) & mask;
            }
        }

        /// <summary>
        /// Corrects an insert slot found through the trailing group of a table smaller than a group. There, the
        /// bytes between the last slot and the mirror are always EMPTY, so a match on one of them wraps (through the
        /// mask) onto a slot that may be full; any free slot of the first group is then used instead. hashbrown's
        /// <c>fix_insert_index</c>.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static int FixInsertSlot(byte[] ctrl, int index)
        {
            if (ControlGroup.IsFull(ctrl[index]))
            {
                Debug.Assert(ctrl.Length - ControlGroup.Width < ControlGroup.Width, "Only tables smaller than a group need the fix.");
                var bits = ControlGroup.MatchEmptyOrDeleted(ControlGroup.Load(ref MemoryMarshal.GetArrayDataReference(ctrl), 0));
                index = ControlGroup.LowestSetBit(bits);
            }

            return index;
        }

        /// <summary>
        /// Writes a control byte and its mirror. For a slot in the first group of a large table the mirror is at
        /// <c>buckets + index</c>; for a table smaller than a group it is at <c>Width + index</c>; otherwise the
        /// formula yields <c>index</c> itself. hashbrown's <c>set_ctrl</c>.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static void SetCtrl(byte[] ctrl, int mask, int index, byte value)
        {
            var mirror = ((index - ControlGroup.Width) & mask) + ControlGroup.Width;
            ctrl[index] = value;
            ctrl[mirror] = value;
        }

        /// <summary>
        /// Accounts for an insert into an EMPTY or DELETED slot and tags it. Only an EMPTY slot uses up growth (its
        /// low bit is set; DELETED's is not). hashbrown's <c>record_item_insert_at</c>.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        void RecordItemInsertAt(int index, byte old, ulong hash)
        {
            Debug.Assert(ControlGroup.IsFull(old) == false);
            growthLeft -= old & 1;
            Debug.Assert(growthLeft >= 0, "An insert into an EMPTY slot needs growth left.");
            SetCtrl(ctrl, bucketMask, index, ControlGroup.Tag(hash));
            items++;
        }

        /// <summary>
        /// Removes the entry in a full slot. The slot becomes EMPTY (returning its growth) unless it lies in a run of
        /// at least <see cref="ControlGroup.Width"/> non-EMPTY slots, counting the slots just before it and from it on:
        /// then some probe may have scanned a whole group of this run without seeing an EMPTY byte and gone on, so the
        /// slot becomes a DELETED tombstone to keep such probes going. Tables smaller than a group never get
        /// tombstones. hashbrown's <c>erase</c>.
        /// </summary>
        void Erase(int index)
        {
            Debug.Assert(ControlGroup.IsFull(ctrl[index]));

            ref var control = ref MemoryMarshal.GetArrayDataReference(ctrl);
            var indexBefore = (index - ControlGroup.Width) & bucketMask;
            var emptyBefore = ControlGroup.MatchEmpty(ControlGroup.Load(ref control, indexBefore));
            var emptyAfter = ControlGroup.MatchEmpty(ControlGroup.Load(ref control, index));

            byte value;
            if (ControlGroup.LeadingZeros(emptyBefore) + ControlGroup.TrailingZeros(emptyAfter) >= ControlGroup.Width)
            {
                value = ControlGroup.Deleted;
            }
            else
            {
                growthLeft++;
                value = ControlGroup.Empty;
            }

            SetCtrl(ctrl, bucketMask, index, value);
            items--;

            if (RuntimeHelpers.IsReferenceOrContainsReferences<TEntry>())
                entries[index] = default;
        }

        /// <summary>
        /// Makes room for <paramref name="additional"/> more entries: rehashes in place when at most half the
        /// capacity would be live (the shortage comes from tombstones), and otherwise resizes to at least the next
        /// size up, so that delete-heavy workloads do not rehash over and over. hashbrown's
        /// <c>reserve_rehash</c>.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        void ReserveRehash<THasher>(int additional, THasher hasher)
            where THasher : struct, IEntryHasher<TEntry>
        {
            var newItems = (long)items + additional;
            var fullCapacity = BucketMaskToCapacity(bucketMask);

            if (newItems <= fullCapacity / 2)
            {
                RehashInPlace(hasher);
                return;
            }

            if (newItems > BucketMaskToCapacity(MaxBuckets - 1))
                throw new InvalidOperationException("Hash table capacity overflow.");

            Resize((int)Math.Max(newItems, fullCapacity + 1), hasher);
        }

        /// <summary>
        /// Moves every entry into a new allocation sized for <paramref name="capacity"/>. A fresh table has no
        /// tombstones, so each entry goes to the first EMPTY slot of its probe sequence. hashbrown's
        /// <c>resize</c>.
        /// </summary>
        void Resize<THasher>(int capacity, THasher hasher)
            where THasher : struct, IEntryHasher<TEntry>
        {
            var buckets = CapacityToBuckets(capacity);
            var newMask = buckets - 1;
            var newCtrl = AllocateControl(buckets);
            var newEntries = GC.AllocateUninitializedArray<TEntry>(buckets);

            var oldEntries = entries;
            ref var oldControl = ref MemoryMarshal.GetArrayDataReference(ctrl);

            // Aligned groups over the old slots; in a table smaller than a group the bytes past the last slot are
            // EMPTY, and the empty singleton has no slots at all. Like hashbrown's FullBucketsIndices, count down the
            // entries and stop after the last one instead of scanning the groups past it.
            var remaining = items;
            for (var groupBase = 0; remaining > 0 && groupBase < oldEntries.Length; groupBase += ControlGroup.Width)
            {
                for (var bits = ControlGroup.MatchFull(ControlGroup.Load(ref oldControl, groupBase)); bits != 0; bits = ControlGroup.RemoveLowestBit(bits))
                {
                    var from = groupBase + ControlGroup.LowestSetBit(bits);
                    remaining--;
                    ref var entry = ref oldEntries[from];
                    var hash = hasher.Hash(in entry);
                    var to = FindInsertSlot(newCtrl, newMask, hash);
                    SetCtrl(newCtrl, newMask, to, ControlGroup.Tag(hash));
                    newEntries[to] = entry;
                }
            }

            ctrl = newCtrl;
            entries = newEntries;
            bucketMask = newMask;
            growthLeft = BucketMaskToCapacity(newMask) - items;
        }

        /// <summary>
        /// Reclaims tombstones without reallocating: marks every full slot DELETED ("to place") and every free slot
        /// EMPTY, then re-inserts each DELETED entry at the first free slot of its probe sequence, leaving it in place
        /// when that slot is in the same probe group, and swapping with a not-yet-placed entry when the target holds
        /// one. hashbrown's <c>rehash_in_place</c>. The hasher must not throw: an exception leaves entries marked
        /// DELETED.
        /// </summary>
        void RehashInPlace<THasher>(THasher hasher)
            where THasher : struct, IEntryHasher<TEntry>
        {
            PrepareRehashInPlace();

            var buckets = entries.Length;
            var slots = entries;
            for (var i = 0; i < buckets; i++)
            {
                if (ctrl[i] != ControlGroup.Deleted)
                    continue;

                while (true)
                {
                    var hash = hasher.Hash(in slots[i]);
                    var newI = FindInsertSlot(ctrl, bucketMask, hash);

                    // A probe scans whole groups starting at the probe start, so a slot in the same group (relative to
                    // that start) as the ideal one is found just as fast: leave the entry where it is.
                    if (IsInSameGroup(i, newI, hash))
                    {
                        SetCtrl(ctrl, bucketMask, i, ControlGroup.Tag(hash));
                        break;
                    }

                    var previous = ctrl[newI];
                    SetCtrl(ctrl, bucketMask, newI, ControlGroup.Tag(hash));

                    if (previous == ControlGroup.Empty)
                    {
                        SetCtrl(ctrl, bucketMask, i, ControlGroup.Empty);
                        slots[newI] = slots[i];
                        if (RuntimeHelpers.IsReferenceOrContainsReferences<TEntry>())
                            slots[i] = default;

                        break;
                    }

                    // The target holds an entry still to be placed: swap and place that one next.
                    Debug.Assert(previous == ControlGroup.Deleted);
                    (slots[i], slots[newI]) = (slots[newI], slots[i]);
                }
            }

            growthLeft = BucketMaskToCapacity(bucketMask) - items;
        }

        /// <summary>
        /// Converts every control byte for an in-place rehash (full to DELETED, EMPTY and DELETED to EMPTY), a group
        /// at a time, then refreshes the mirror bytes. hashbrown's <c>prepare_rehash_in_place</c>.
        /// </summary>
        void PrepareRehashInPlace()
        {
            var buckets = entries.Length;
            ref var control = ref MemoryMarshal.GetArrayDataReference(ctrl);

            for (var i = 0; i < buckets; i += ControlGroup.Width)
                ControlGroup.Store(ControlGroup.ConvertSpecialToEmptyAndFullToDeleted(ControlGroup.Load(ref control, i)), ref control, i);

            if (buckets < ControlGroup.Width)
                ctrl.AsSpan(0, buckets).CopyTo(ctrl.AsSpan(ControlGroup.Width));
            else
                ctrl.AsSpan(0, ControlGroup.Width).CopyTo(ctrl.AsSpan(buckets));
        }

        /// <summary>
        /// Gets whether two slots fall in the same probe group of <paramref name="hash"/>'s probe sequence. hashbrown's
        /// <c>is_in_same_group</c>.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        bool IsInSameGroup(int i, int newI, ulong hash)
        {
            var probeStart = (int)hash & bucketMask;
            return ((i - probeStart) & bucketMask) / ControlGroup.Width == ((newI - probeStart) & bucketMask) / ControlGroup.Width;
        }

        /// <summary>
        /// Allocates the control bytes of a table with <paramref name="buckets"/> slots, all EMPTY, plus the mirror
        /// group.
        /// </summary>
        static byte[] AllocateControl(int buckets)
        {
            var control = GC.AllocateUninitializedArray<byte>(buckets + ControlGroup.Width);
            control.AsSpan().Fill(ControlGroup.Empty);
            return control;
        }

    }

}

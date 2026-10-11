using System;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace Eggnog.Relations
{

    /// <summary>
    /// The classes a child context merged, over the ids that are canonical in its parent: the context's own level of
    /// the layered union-find (colored-contexts spec §5.2, C2, L2, B7). <c>find_C = Map ∘ find_P</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A class table. Each slot holds one class: its canonical id (the minimum member), its member list in insertion
    /// order, and its member count (0 marks a free slot). <c>classOf</c> maps every member to its slot. Ids in no class
    /// are their own class in this level, so <see cref="Map"/> is the identity for them. Invariants (I5, I6): the classes
    /// partition <c>classOf</c>'s keys; every class has at least two members; <c>canonical = min(members)</c>; and, once
    /// the owning context has caught up with its parent, every member is canonical in the parent. Each helper keeps
    /// them: a class left with one member dissolves, and a class that loses its canonical recomputes the minimum.
    /// </para>
    /// <para>
    /// Why a class table (§5.2): <see cref="Map"/> is one hash probe and one load and never writes, so the context's own
    /// rule tasks may read it while nothing merges; and re-keying after a parent change (<see cref="Rekey"/>) and the
    /// lift's member maps (L2) need the member lists anyway. Union merges the smaller class into the larger, relinking the
    /// smaller's members, so a sequence of unions costs O(n log n) in total.
    /// </para>
    /// <para>
    /// Threading: <see cref="Map"/>, <see cref="TryGetClass"/> and the accessors are safe from many threads while no
    /// thread calls <see cref="Union"/> or <see cref="Rekey"/>, which need exclusive access (the owning context's merge
    /// and catch-up). Once <see cref="Freeze"/>d it never changes. Nothing is allocated before the first union.
    /// </para>
    /// </remarks>
    internal sealed class UnionFindOverlay
    {

        /// <summary>
        /// The capacity of a new class's member list.
        /// </summary>
        const int InitialMembers = 4;

        /// <summary>
        /// Every member to its slot (<see cref="IdEntry.Payload"/>); <c>null</c> until the first union.
        /// </summary>
        RawHashTable<IdEntry>? classOf;

        /// <summary>
        /// The canonical id (minimum member) of each slot's class.
        /// </summary>
        uint[] canonicals = Array.Empty<uint>();

        /// <summary>
        /// The member list of each slot, in insertion order; a free slot keeps its array for reuse.
        /// </summary>
        uint[]?[] members = Array.Empty<uint[]?>();

        /// <summary>
        /// The member count of each slot; 0 for a free slot.
        /// </summary>
        int[] counts = Array.Empty<int>();

        /// <summary>
        /// The slots freed by dissolving or merged-away classes, reused before new slots.
        /// </summary>
        int[] freeSlots = Array.Empty<int>();

        int freeCount;
        int slotCount;
        int classCount;
        int version;
        volatile bool frozen;

        /// <summary>
        /// Gets whether no class exists: <see cref="Map"/> is then the identity, and the layered find skips this level.
        /// </summary>
        public bool IsEmpty
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => classCount == 0;
        }

        /// <summary>
        /// Gets the number of members over all classes.
        /// </summary>
        public int MemberCount => classOf?.Count ?? 0;

        /// <summary>
        /// Gets the number of classes.
        /// </summary>
        public int ClassCount => classCount;

        /// <summary>
        /// Gets the number of slots ever used: valid slots are <c>[0, SlotCount)</c>, of which those with a count of 0
        /// are free.
        /// </summary>
        public int SlotCount => slotCount;

        /// <summary>
        /// Gets a counter that moves with every effective <see cref="Union"/> and every <see cref="Rekey"/> that changed
        /// a class (X6, X29): a cache derived from the classes (the lift's member map) is valid while it is unchanged.
        /// </summary>
        public int Version => version;

        /// <summary>
        /// Gets whether the overlay is frozen and can no longer change.
        /// </summary>
        public bool IsFrozen => frozen;

        /// <summary>
        /// Maps an id canonical in the parent to its canonical id in this level: its class's canonical, or itself when it
        /// is in no class. One hash probe and one load; never writes.
        /// </summary>
        /// <param name="parentCanonical">An id canonical in the parent.</param>
        /// <returns>The canonical id in the owning context.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public uint Map(uint parentCanonical)
        {
            var table = classOf;
            if (table is null)
                return parentCanonical;

            var match = new IdEntryMatch(parentCanonical);
            return table.TryFind(FxHasher.Hash(parentCanonical), ref match, out var entry) ? canonicals[entry.Payload] : parentCanonical;
        }

        /// <summary>
        /// Gets the slot of <paramref name="id"/>'s class, if it is a member of one. One hash probe.
        /// </summary>
        /// <param name="id">The id.</param>
        /// <param name="slot">The slot, or -1.</param>
        /// <returns><c>true</c> if the id is a member.</returns>
        public bool TryGetClass(uint id, out int slot)
        {
            slot = SlotOf(id);
            return slot >= 0;
        }

        /// <summary>
        /// Gets the canonical id (the minimum member) of the class in <paramref name="slot"/>.
        /// </summary>
        /// <param name="slot">An occupied slot.</param>
        /// <returns>The canonical id.</returns>
        /// <exception cref="ArgumentOutOfRangeException">The slot is not below <see cref="SlotCount"/>.</exception>
        public uint CanonicalOf(int slot)
        {
            CheckSlot(slot);
            return canonicals[slot];
        }

        /// <summary>
        /// Gets the members of the class in <paramref name="slot"/>, in insertion order; empty for a free slot. Valid
        /// until the overlay next changes.
        /// </summary>
        /// <param name="slot">A slot.</param>
        /// <returns>The members.</returns>
        /// <exception cref="ArgumentOutOfRangeException">The slot is not below <see cref="SlotCount"/>.</exception>
        public ReadOnlySpan<uint> MembersOf(int slot)
        {
            CheckSlot(slot);
            var count = counts[slot];
            return count == 0 ? ReadOnlySpan<uint>.Empty : new ReadOnlySpan<uint>(members[slot], 0, count);
        }

        /// <summary>
        /// Gets the member count of the class in <paramref name="slot"/>; 0 for a free slot.
        /// </summary>
        /// <param name="slot">A slot.</param>
        /// <returns>The count.</returns>
        /// <exception cref="ArgumentOutOfRangeException">The slot is not below <see cref="SlotCount"/>.</exception>
        public int CountOf(int slot)
        {
            CheckSlot(slot);
            return counts[slot];
        }

        /// <summary>
        /// Merges the classes of <paramref name="a"/> and <paramref name="b"/>, both canonical in the parent
        /// (§5.2): a new class, a new member, or two classes merged, the smaller into the larger.
        /// </summary>
        /// <remarks>
        /// The canonical of each side is its class's canonical, or the id itself when it is in no class; the smaller
        /// wins. O(1) expected, or O(smaller class) when two classes merge.
        /// </remarks>
        /// <param name="a">An id canonical in the parent.</param>
        /// <param name="b">Another id canonical in the parent.</param>
        /// <param name="winner">The canonical of the merged class; when nothing changed, the common canonical.</param>
        /// <param name="loser">The canonical that stopped being canonical; when nothing changed, the common canonical.</param>
        /// <returns><c>true</c> if the two were in different classes.</returns>
        /// <exception cref="InvalidOperationException">The overlay is frozen.</exception>
        public bool Union(uint a, uint b, out uint winner, out uint loser)
        {
            ThrowIfFrozen();
            var ka = SlotOf(a);
            var kb = SlotOf(b);
            var ca = ka >= 0 ? canonicals[ka] : a;
            var cb = kb >= 0 ? canonicals[kb] : b;
            if (ca == cb)
            {
                winner = ca;
                loser = ca;
                return false;
            }

            winner = Math.Min(ca, cb);
            loser = Math.Max(ca, cb);
            if (ka < 0 && kb < 0)
                NewClass(a, b);
            else if (ka < 0)
                AddMember(kb, a);
            else if (kb < 0)
                AddMember(ka, b);
            else
                MergeClasses(ka, kb);

            version++;
            return true;
        }

        /// <summary>
        /// Re-keys the overlay after the parent displaced <paramref name="displaced"/> into
        /// <paramref name="parentCanonical"/> (§5.6): the displaced id's class merges with the parent canonical's, and
        /// the displaced member is replaced by the parent canonical. Reports the ids that were canonical in the owning
        /// context before and are not after, which the caller journals.
        /// </summary>
        /// <remarks>
        /// <para>
        /// When the displaced id is in no class it was canonical here and is no longer (its parent class now holds a
        /// smaller id): it is the one id reported, an import, which the caller skips if it journaled it already. Otherwise
        /// the class's old canonical and the parent canonical's old canonical are each reported when they differ from the
        /// merged class's canonical. At most two ids are reported.
        /// </para>
        /// <para>
        /// Within a batch of parent displacements the caller passes each id's final parent canonical, so the overlay is
        /// keyed by parent canonicals again once the batch is done (I6). O(1) expected plus the classes' sizes.
        /// </para>
        /// </remarks>
        /// <param name="displaced">The id the parent displaced; canonical in the parent before.</param>
        /// <param name="parentCanonical">Its canonical id in the parent now, which is smaller.</param>
        /// <param name="lost">Receives the ids that stopped being canonical; at least two cells.</param>
        /// <returns>The number of ids written to <paramref name="lost"/>: 0, 1 or 2.</returns>
        /// <exception cref="ArgumentException"><paramref name="lost"/> has fewer than two cells.</exception>
        /// <exception cref="InvalidOperationException">The overlay is frozen.</exception>
        public int Rekey(uint displaced, uint parentCanonical, Span<uint> lost)
        {
            ThrowIfFrozen();
            if (lost.Length < 2)
                throw new ArgumentException("Re-keying can report two ids; the span needs room for them.", nameof(lost));

            var kd = SlotOf(displaced);
            if (kd < 0)
            {
                lost[0] = displaced;
                return 1;
            }

            var kp = SlotOf(parentCanonical);
            var cdOld = canonicals[kd];
            var cpOld = kp >= 0 ? canonicals[kp] : parentCanonical;
            version++;
            if (kd == kp)
            {
                RemoveMember(kd, displaced, out _, out _);
                return 0;
            }

            RemoveMember(kd, displaced, out var dissolved, out var remaining);
            int k;
            if (dissolved)
                k = kp >= 0 ? AddMember(kp, remaining) : NewClass(parentCanonical, remaining);
            else
                k = kp >= 0 ? MergeClasses(kd, kp) : AddMember(kd, parentCanonical);

            var cNew = canonicals[k];
            var n = 0;
            if (cdOld != cNew)
                lost[n++] = cdOld;

            if (cpOld != cNew && cpOld != cdOld)
                lost[n++] = cpOld;

            return n;
        }

        /// <summary>
        /// Freezes the overlay: <see cref="Union"/> and <see cref="Rekey"/> throw from now on, so frozen contexts and
        /// their concurrent children read it without synchronization.
        /// </summary>
        public void Freeze() => frozen = true;

        /// <summary>
        /// Drops every class and its storage, when the owning context is disposed. The overlay then maps every id to
        /// itself.
        /// </summary>
        internal void Release()
        {
            classOf = null;
            canonicals = Array.Empty<uint>();
            members = Array.Empty<uint[]?>();
            counts = Array.Empty<int>();
            freeSlots = Array.Empty<int>();
            freeCount = 0;
            slotCount = 0;
            classCount = 0;
        }

        /// <summary>
        /// Gets the slot of <paramref name="id"/>'s class, or -1.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        int SlotOf(uint id)
        {
            var table = classOf;
            if (table is null)
                return -1;

            var match = new IdEntryMatch(id);
            return table.TryFind(FxHasher.Hash(id), ref match, out var entry) ? (int)entry.Payload : -1;
        }

        /// <summary>
        /// Creates the class <c>{a, b}</c>. O(1) amortized.
        /// </summary>
        /// <returns>Its slot.</returns>
        int NewClass(uint a, uint b)
        {
            Debug.Assert(a != b && SlotOf(a) < 0 && SlotOf(b) < 0);
            var slot = AllocateSlot();
            var list = members[slot] ??= new uint[InitialMembers];
            list[0] = a;
            list[1] = b;
            counts[slot] = 2;
            canonicals[slot] = Math.Min(a, b);
            InsertKey(a, slot);
            InsertKey(b, slot);
            classCount++;
            return slot;
        }

        /// <summary>
        /// Appends <paramref name="x"/> to the class in <paramref name="slot"/>. O(1) amortized.
        /// </summary>
        /// <returns>The slot.</returns>
        int AddMember(int slot, uint x)
        {
            Debug.Assert(counts[slot] >= 2 && SlotOf(x) < 0);
            var count = counts[slot];
            var list = EnsureCapacity(slot, count + 1);
            list[count] = x;
            counts[slot] = count + 1;
            InsertKey(x, slot);
            if (x < canonicals[slot])
                canonicals[slot] = x;

            return slot;
        }

        /// <summary>
        /// Merges the smaller of two classes into the larger, relinking the smaller's members. O(smaller class).
        /// </summary>
        /// <returns>The surviving slot.</returns>
        int MergeClasses(int s, int t)
        {
            Debug.Assert(s != t && counts[s] >= 2 && counts[t] >= 2);
            var big = s;
            var small = t;
            if (counts[small] > counts[big])
                (big, small) = (small, big);

            var source = members[small]!;
            var n = counts[small];
            var count = counts[big];
            var target = EnsureCapacity(big, count + n);
            var table = classOf!;
            for (var i = 0; i < n; i++)
            {
                var m = source[i];
                target[count + i] = m;
                var match = new IdEntryMatch(m);
                ref var entry = ref table.FindRef(FxHasher.Hash(m), ref match, out var found);
                Debug.Assert(found, "a member is missing from the class map");
                entry = new IdEntry(m, (uint)big);
            }

            counts[big] = count + n;
            if (canonicals[small] < canonicals[big])
                canonicals[big] = canonicals[small];

            FreeSlot(small);
            return big;
        }

        /// <summary>
        /// Removes <paramref name="x"/> from the class in <paramref name="slot"/>, keeping the order of the others. A
        /// class left with one member dissolves (its slot is freed and the last member leaves the map); a class that
        /// loses its canonical recomputes its minimum. O(class size).
        /// </summary>
        /// <param name="slot">The class's slot.</param>
        /// <param name="x">A member.</param>
        /// <param name="dissolved">Whether the class dissolved.</param>
        /// <param name="remaining">When it dissolved, its last member; otherwise 0.</param>
        void RemoveMember(int slot, uint x, out bool dissolved, out uint remaining)
        {
            var list = members[slot]!;
            var count = counts[slot];
            var index = Array.IndexOf(list, x, 0, count);
            Debug.Assert(index >= 0, "the id is not a member of the class");
            Array.Copy(list, index + 1, list, index, count - index - 1);
            count--;
            counts[slot] = count;
            RemoveKey(x);
            if (count == 1)
            {
                remaining = list[0];
                RemoveKey(remaining);
                FreeSlot(slot);
                dissolved = true;
                return;
            }

            dissolved = false;
            remaining = 0;
            if (canonicals[slot] == x)
            {
                var min = list[0];
                for (var i = 1; i < count; i++)
                    min = Math.Min(min, list[i]);

                canonicals[slot] = min;
            }
        }

        /// <summary>
        /// Takes a free slot, or a new one at the end, growing the slot arrays geometrically.
        /// </summary>
        int AllocateSlot()
        {
            if (freeCount > 0)
                return freeSlots[--freeCount];

            if (slotCount == canonicals.Length)
            {
                var capacity = Math.Max(4, slotCount * 2);
                Array.Resize(ref canonicals, capacity);
                Array.Resize(ref members, capacity);
                Array.Resize(ref counts, capacity);
            }

            return slotCount++;
        }

        /// <summary>
        /// Frees a slot whose class dissolved or merged away; its member array is kept for reuse.
        /// </summary>
        void FreeSlot(int slot)
        {
            counts[slot] = 0;
            if (freeCount == freeSlots.Length)
                Array.Resize(ref freeSlots, Math.Max(4, freeCount * 2));

            freeSlots[freeCount++] = slot;
            classCount--;
        }

        /// <summary>
        /// Makes room for <paramref name="needed"/> members in the class in <paramref name="slot"/>.
        /// </summary>
        /// <returns>The member array.</returns>
        uint[] EnsureCapacity(int slot, int needed)
        {
            var list = members[slot]!;
            if (list.Length < needed)
            {
                Array.Resize(ref list, Math.Max(needed, list.Length * 2));
                members[slot] = list;
            }

            return list;
        }

        /// <summary>
        /// Maps a new member to its slot.
        /// </summary>
        void InsertKey(uint id, int slot)
        {
            var table = classOf ??= new RawHashTable<IdEntry>();
            table.InsertUnique(FxHasher.Hash(id), new IdEntry(id, (uint)slot), default(IdEntryHasher));
        }

        /// <summary>
        /// Removes a member from the map.
        /// </summary>
        void RemoveKey(uint id)
        {
            var match = new IdEntryMatch(id);
            var removed = classOf!.Remove(FxHasher.Hash(id), ref match, out _);
            Debug.Assert(removed, "a member is missing from the class map");
        }

        void CheckSlot(int slot)
        {
            if ((uint)slot >= (uint)slotCount)
                throw new ArgumentOutOfRangeException(nameof(slot), slot, $"The overlay has {slotCount} slots.");
        }

        void ThrowIfFrozen()
        {
            if (frozen)
                ThrowFrozen();
        }

        [DoesNotReturn]
        [MethodImpl(MethodImplOptions.NoInlining)]
        static void ThrowFrozen()
        {
            throw new InvalidOperationException("The union-find of a frozen context cannot be written.");
        }

    }

}

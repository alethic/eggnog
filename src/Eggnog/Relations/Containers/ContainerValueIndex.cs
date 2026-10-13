using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Eggnog.Relations
{

    /// <summary>
    /// The element index of a <see cref="ContainerEnv{TContainer}"/>: for each value, the ids of the containers that
    /// hold it. egglog's <c>val_index: DashMap&lt;Value, IndexSet&lt;Value&gt;&gt;</c>
    /// (core-relations/src/containers/mod.rs:312-313), which an incremental rebuild uses to find the containers that
    /// mention a newly displaced id, and the dirty-id closure to climb from a container to the containers holding it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Exact, unlike egglog's. egglog only ever adds to its index, so it keeps ids of containers that were rebuilt away
    /// and misses the new id after a rebuild renames a container without changing its contents (mod.rs:553-558,
    /// 618-623). Here every removal of a container from the hash-cons removes its occurrences and every rename moves
    /// them, so an id is listed under a value exactly when a live container with that id holds the value. Colored
    /// contexts rely on that: a child's eq-container guard asks whether any live container holds an id the child
    /// displaced (<see cref="ContainsElement"/>, <see cref="EqContainerGuard"/>).
    /// </para>
    /// <para>
    /// Each value maps to a multiset of ids counted by occurrence (<see cref="ValueIndexEntry"/>), so removals undo
    /// additions exactly even when a container holds a value twice or two containers share an id. The caller removes a
    /// container's occurrences by visiting the same contents it added; containers are immutable, so it always can.
    /// </para>
    /// <para>
    /// Threading: sharded by the value's hash, each shard a <see cref="RawHashTable{TEntry}"/> behind its own lock, so
    /// concurrent interning and parallel rebuild tasks contend only within a shard. Every member is thread-safe.
    /// Costs: expected O(1) per call, plus the size of the multiset for <see cref="CollectContaining"/>.
    /// </para>
    /// </remarks>
    internal sealed class ContainerValueIndex
    {

        readonly ShardData shardData;
        readonly RawHashTable<ValueIndexEntry>[] shards;

        /// <summary>
        /// Initializes an empty index.
        /// </summary>
        /// <param name="shardData">The shard layout.</param>
        public ContainerValueIndex(ShardData shardData)
        {
            this.shardData = shardData;
            shards = new RawHashTable<ValueIndexEntry>[shardData.Count];
            for (var i = 0; i < shards.Length; i++)
                shards[i] = new RawHashTable<ValueIndexEntry>();
        }

        ContainerValueIndex(ShardData shardData, RawHashTable<ValueIndexEntry>[] shards)
        {
            this.shardData = shardData;
            this.shards = shards;
        }

        /// <summary>
        /// Gets the number of distinct values indexed. Takes each shard's lock in turn. O(shards).
        /// </summary>
        public int ValueCount
        {
            get
            {
                var count = 0;
                foreach (var shard in shards)
                    lock (shard)
                        count += shard.Count;

                return count;
            }
        }

        /// <summary>
        /// Gets whether some live container holds <paramref name="element"/> (the index is exact): the probe of the
        /// eq-container guard of a child context's rebuild (<see cref="EqContainerGuard"/>). One lock and one
        /// expected-O(1) probe.
        /// </summary>
        /// <param name="element">The value.</param>
        /// <returns><c>true</c> if a container holds it.</returns>
        public bool ContainsElement(uint element)
        {
            var hash = FxHasher.Hash(element);
            var shard = shards[shardData.ShardOf(hash)];
            lock (shard)
            {
                var match = new ValueIndexElementMatch(element);
                return shard.Find(hash, ref match) >= 0;
            }
        }

        /// <summary>
        /// Records one occurrence of <paramref name="element"/> in the container <paramref name="id"/>. egglog's
        /// <c>val_index.entry(val).or_default().insert(id)</c>.
        /// </summary>
        /// <param name="element">The value.</param>
        /// <param name="id">The container id.</param>
        public void Add(uint element, uint id)
        {
            var hash = FxHasher.Hash(element);
            var shard = shards[shardData.ShardOf(hash)];
            lock (shard)
            {
                var match = new ValueIndexElementMatch(element);
                var slot = shard.FindOrFindInsertSlot(hash, ref match, default(ValueIndexEntryHasher));
                if (slot < 0)
                {
                    shard.InsertInSlot(hash, ~slot, new ValueIndexEntry { Element = element, Id = id, Count = 1 });
                    return;
                }

                AddTo(ref shard.EntryAt(slot), id);
            }
        }

        /// <summary>
        /// Removes one occurrence of <paramref name="element"/> in the container <paramref name="id"/>, which must have
        /// been added. The value's slot goes when its last occurrence does.
        /// </summary>
        /// <param name="element">The value.</param>
        /// <param name="id">The container id.</param>
        public void Remove(uint element, uint id)
        {
            var hash = FxHasher.Hash(element);
            var shard = shards[shardData.ShardOf(hash)];
            lock (shard)
            {
                var match = new ValueIndexElementMatch(element);
                var slot = shard.Find(hash, ref match);
                if (slot < 0)
                {
                    Debug.Assert(false, $"value {element} of container {id} is not indexed");
                    return;
                }

                if (RemoveFrom(ref shard.EntryAt(slot), id))
                    shard.RemoveAt(slot);
            }
        }

        /// <summary>
        /// Moves one occurrence of <paramref name="element"/> from the container id <paramref name="oldId"/> to
        /// <paramref name="newId"/>, under one lock: what egglog's merge does with <c>swap_remove(old)</c> and
        /// <c>insert(result)</c> (mod.rs:422-426), and what its id-only rename forgets to do (mod.rs:553-558).
        /// </summary>
        /// <param name="element">The value.</param>
        /// <param name="oldId">The id the occurrence is recorded under.</param>
        /// <param name="newId">The id to record it under.</param>
        public void Rename(uint element, uint oldId, uint newId)
        {
            if (oldId == newId)
                return;

            var hash = FxHasher.Hash(element);
            var shard = shards[shardData.ShardOf(hash)];
            lock (shard)
            {
                var match = new ValueIndexElementMatch(element);
                var slot = shard.Find(hash, ref match);
                if (slot < 0)
                {
                    Debug.Assert(false, $"value {element} of container {oldId} is not indexed");
                    return;
                }

                // The common case, a value held once by the one container being renamed, rewrites the id in place.
                ref var entry = ref shard.EntryAt(slot);
                if (entry.Many is null && entry.Id == oldId && entry.Count == 1)
                {
                    entry.Id = newId;
                    return;
                }

                // Adding first keeps the slot nonempty, so it never has to be removed and reinserted.
                AddTo(ref entry, newId);
                var emptied = RemoveFrom(ref entry, oldId);
                Debug.Assert(emptied == false, "a slot holding the new id cannot become empty");
            }
        }

        /// <summary>
        /// Adds to <paramref name="output"/> the ids of the containers holding <paramref name="element"/>. egglog's
        /// <c>val_index.get(value)</c> followed by <c>extend</c>. O(1) plus the number of ids.
        /// </summary>
        /// <param name="element">The value.</param>
        /// <param name="output">Receives the ids not already in it.</param>
        public void CollectContaining(uint element, OrderedValueSet output)
        {
            var hash = FxHasher.Hash(element);
            var shard = shards[shardData.ShardOf(hash)];
            lock (shard)
            {
                var match = new ValueIndexElementMatch(element);
                var slot = shard.Find(hash, ref match);
                if (slot < 0)
                    return;

                ref var entry = ref shard.EntryAt(slot);
                if (entry.Many is { } many)
                {
                    foreach (var id in many.Keys)
                        output.Add(id);
                }
                else
                {
                    output.Add(entry.Id);
                }
            }
        }

        /// <summary>
        /// Gets how many occurrences of <paramref name="element"/> are recorded under the container
        /// <paramref name="id"/>.
        /// </summary>
        /// <param name="element">The value.</param>
        /// <param name="id">The container id.</param>
        /// <returns>The count, zero if none.</returns>
        public int CountOf(uint element, uint id)
        {
            var hash = FxHasher.Hash(element);
            var shard = shards[shardData.ShardOf(hash)];
            lock (shard)
            {
                var match = new ValueIndexElementMatch(element);
                var slot = shard.Find(hash, ref match);
                if (slot < 0)
                    return 0;

                ref var entry = ref shard.EntryAt(slot);
                if (entry.Many is { } many)
                    return many.TryGetValue(id, out var count) ? count : 0;

                return entry.Id == id ? entry.Count : 0;
            }
        }

        /// <summary>
        /// Lists every recorded (value, container id) pair with its occurrence count, for invariant checks. Takes each
        /// shard's lock in turn; allocates. O(size).
        /// </summary>
        /// <returns>The pairs and counts.</returns>
        public Dictionary<(uint Element, uint Id), int> Snapshot()
        {
            var result = new Dictionary<(uint Element, uint Id), int>();
            foreach (var shard in shards)
            {
                lock (shard)
                {
                    for (var slot = shard.NextFullSlot(0); slot >= 0; slot = shard.NextFullSlot(slot + 1))
                    {
                        ref var entry = ref shard.EntryAt(slot);
                        if (entry.Many is { } many)
                        {
                            Debug.Assert(many.Count >= 2, "a multiset of one id should be inline");
                            foreach (var (id, count) in many)
                            {
                                Debug.Assert(count > 0, "a stored count is positive");
                                result.Add((entry.Element, id), count);
                            }
                        }
                        else
                        {
                            Debug.Assert(entry.Count > 0, "a stored count is positive");
                            result.Add((entry.Element, entry.Id), entry.Count);
                        }
                    }
                }
            }

            return result;
        }

        /// <summary>
        /// Creates an independent deep copy. Requires that no thread changes the index meanwhile. O(size).
        /// </summary>
        /// <returns>The copy.</returns>
        public ContainerValueIndex Clone()
        {
            var copy = new RawHashTable<ValueIndexEntry>[shards.Length];
            for (var i = 0; i < shards.Length; i++)
            {
                var shard = shards[i].Clone();
                for (var slot = shard.NextFullSlot(0); slot >= 0; slot = shard.NextFullSlot(slot + 1))
                {
                    ref var entry = ref shard.EntryAt(slot);
                    if (entry.Many is { } many)
                        entry.Many = new Dictionary<uint, int>(many);
                }

                copy[i] = shard;
            }

            return new ContainerValueIndex(shardData, copy);
        }

        /// <summary>
        /// Adds one occurrence of <paramref name="id"/> to a stored slot.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static void AddTo(ref ValueIndexEntry entry, uint id)
        {
            if (entry.Many is { } many)
            {
                CollectionsMarshal.GetValueRefOrAddDefault(many, id, out _)++;
                return;
            }

            if (entry.Id == id)
            {
                entry.Count++;
                return;
            }

            entry.Many = new Dictionary<uint, int>(4)
            {
                [entry.Id] = entry.Count,
                [id] = 1,
            };
        }

        /// <summary>
        /// Removes one occurrence of <paramref name="id"/> from a stored slot.
        /// </summary>
        /// <returns><c>true</c> if the slot is now empty and must be removed.</returns>
        static bool RemoveFrom(ref ValueIndexEntry entry, uint id)
        {
            if (entry.Many is { } many)
            {
                ref var count = ref CollectionsMarshal.GetValueRefOrNullRef(many, id);
                if (Unsafe.IsNullRef(ref count))
                {
                    Debug.Assert(false, $"container {id} is not indexed under value {entry.Element}");
                    return false;
                }

                if (--count == 0)
                {
                    many.Remove(id);
                    if (many.Count == 1)
                    {
                        foreach (var (rest, restCount) in many)
                        {
                            entry.Id = rest;
                            entry.Count = restCount;
                        }

                        entry.Many = null;
                    }
                }

                return false;
            }

            if (entry.Id != id)
            {
                Debug.Assert(false, $"container {id} is not indexed under value {entry.Element}");
                return false;
            }

            return --entry.Count == 0;
        }

    }

}

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace Eggnog.Relations
{

    internal sealed partial class ContainerEnv<TContainer>
    {

        /// <summary>
        /// Gets the id of <paramref name="container"/>, whose content hash is <paramref name="hash"/>, without interning
        /// it: <see cref="TryGetId(in TContainer, out uint)"/> with the hash computed once by the caller, as a lookup
        /// through a child's view probes one environment per level (<see cref="ContainerLayers"/>). Thread-safe; one
        /// lock and one probe.
        /// </summary>
        /// <param name="container">The container.</param>
        /// <param name="hash">Its content hash (<see cref="IContainerValue{TSelf}.ComputeHash"/>).</param>
        /// <param name="id">Its id, or zero.</param>
        /// <returns><c>true</c> if the container is interned.</returns>
        public bool TryGetId(in TContainer container, ulong hash, out uint id)
        {
            Debug.Assert(hash == container.ComputeHash(), "the content hash is stale");

            var shard = toId[shardData.ShardOf(hash)];
            lock (shard)
                return TryFindId(shard, container, hash, out id);
        }

        /// <summary>
        /// <see cref="TryGetId(in TContainer, ulong, out uint)"/> without the shard lock, for an environment nothing
        /// writes any more: a frozen context's layer (<see cref="ContainerLayer{TContainer}"/>), whose readers on any
        /// thread see what the volatile write of the context's frozen flag published. One probe.
        /// </summary>
        /// <param name="container">The container.</param>
        /// <param name="hash">Its content hash.</param>
        /// <param name="id">Its id, or zero.</param>
        /// <returns><c>true</c> if the container is interned.</returns>
        internal bool TryGetIdUnlocked(in TContainer container, ulong hash, out uint id)
        {
            Debug.Assert(hash == container.ComputeHash(), "the content hash is stale");
            return TryFindId(toId[shardData.ShardOf(hash)], container, hash, out id);
        }

        /// <summary>
        /// <see cref="TryGetContainer"/> without the shard lock, for an environment nothing writes any more (see
        /// <see cref="TryGetIdUnlocked"/>). One probe.
        /// </summary>
        /// <param name="id">The container id.</param>
        /// <param name="container">The container, or <c>default</c>.</param>
        /// <returns><c>false</c> if no container of this type has the id.</returns>
        internal bool TryGetContainerUnlocked(uint id, out TContainer container)
        {
            var hash = FxHasher.Hash(id);
            return TryFindContainer(byId[shardData.ShardOf(hash)], hash, id, out container);
        }

        /// <inheritdoc />
        public override bool ContainsId(uint id) => TryGetContainer(id, out _);

        /// <inheritdoc />
        internal override bool ContainsIdUnlocked(uint id) => TryGetContainerUnlocked(id, out _);

        /// <summary>
        /// Appends to <paramref name="destination"/> every entry stored under <paramref name="id"/>, leaving them in
        /// place: what a child copies when it takes an ancestor's containers under an id into its own layer. Usually one
        /// entry; more where a rebuild renamed one container's id to another's (the type's remarks). Takes the id's
        /// reverse-map shard lock when <paramref name="locked"/>, and none for an environment nothing writes any more
        /// (see <see cref="TryGetIdUnlocked"/>). Thread-safe with the lock; one probe sequence.
        /// </summary>
        /// <param name="id">The id.</param>
        /// <param name="destination">Receives the entries, in probe order.</param>
        /// <param name="locked">Whether to take the shard lock.</param>
        /// <returns>The number of entries appended.</returns>
        internal int CollectById(uint id, List<ContainerEntry<TContainer>> destination, bool locked)
        {
            var hash = FxHasher.Hash(id);
            var shard = byId[shardData.ShardOf(hash)];
            var before = destination.Count;
            var collector = new ContainerIdCollector<TContainer>(id, destination);
            if (locked)
            {
                lock (shard)
                    shard.Find(hash, ref collector);
            }
            else
            {
                shard.Find(hash, ref collector);
            }

            return destination.Count - before;
        }

        /// <summary>
        /// Inserts <paramref name="container"/>, whose content hash is <paramref name="hash"/>, under
        /// <paramref name="id"/> when the environment holds no equal container, merging nothing: the insert of a test
        /// hook that places a container in a child's layer (<see cref="ContainerLayers.Place{TContainer}"/>), where a
        /// rebuild's insert merges (<see cref="InsertOwned(in TContainer, ulong, uint, ExecutionState)"/>). A container
        /// of classes is recorded in the container log while it is active, as every insert is. Thread-safe.
        /// </summary>
        /// <param name="container">The container; it must not change afterwards.</param>
        /// <param name="hash">Its content hash.</param>
        /// <param name="id">The id to insert it under.</param>
        /// <returns><c>false</c>, inserting nothing, if an equal container is present.</returns>
        internal bool TryInsertNew(in TContainer container, ulong hash, uint id)
        {
            Debug.Assert(hash == container.ComputeHash(), "the content hash is stale");

            var index = shardData.ShardOf(hash);
            var shard = toId[index];
            lock (shard)
            {
                var match = new ContainerContentMatch<TContainer>(container, hash);
                var slot = shard.FindOrFindInsertSlot(hash, ref match, default(ContainerContentHasher<TContainer>));
                if (slot >= 0)
                    return false;

                var entry = new ContainerEntry<TContainer>(container, hash, id);
                AddById(entry);
                shard.InsertInSlot(hash, ~slot, entry);
                if (HoldsIds && container.HoldsIds)
                    LogAdded(index, id, hash);
            }

            Index(container, id);
            return true;
        }

        /// <summary>
        /// Returns the id of <paramref name="container"/>, whose content hash is <paramref name="hash"/>, in this
        /// environment, a child context's layer's own (<see cref="ContainerLayer{TContainer}.Own"/>), interning it with
        /// an id minted from <paramref name="ids"/>, the context's id blocks, when it is new here: what a writable child
        /// does with a container of classes its view does not hold (<see cref="ContainerLayers.GetOrInsert{TContainer}"/>),
        /// as egglog's <c>get_or_insert</c> (core-relations/src/containers/mod.rs:366-409) interns in the copy its push
        /// makes. Under the content's hash-cons shard lock it probes again, mints, inserts and records the entry in the
        /// container log while the log is active, so the context's rule tasks that race to intern one content get one
        /// id, and only the winner mints. Thread-safe.
        /// </summary>
        /// <remarks>
        /// The id is local to the context (<see cref="IdBlockAllocator.Contains"/>), so the layer owns it by the
        /// ownership rule, and no ancestor names it. Cost: as <see cref="GetOrInsert"/>, with the id taken from the
        /// context's current block.
        /// </remarks>
        /// <param name="container">The container; it must not change afterwards.</param>
        /// <param name="hash">Its content hash.</param>
        /// <param name="ids">The context's id blocks.</param>
        /// <returns>The container's id.</returns>
        /// <exception cref="OverflowException">The store has no ids left for a new block.</exception>
        internal uint GetOrInsertLocal(in TContainer container, ulong hash, IdBlockAllocator ids)
        {
            Debug.Assert(hash == container.ComputeHash(), "the content hash is stale");

            var index = shardData.ShardOf(hash);
            var shard = toId[index];
            uint id;
            lock (shard)
            {
                var match = new ContainerContentMatch<TContainer>(container, hash);
                var slot = shard.FindOrFindInsertSlot(hash, ref match, default(ContainerContentHasher<TContainer>));
                if (slot >= 0)
                    return shard.EntryAt(slot).Id;

                id = ids.Next();
                var entry = new ContainerEntry<TContainer>(container, hash, id);
                AddById(entry);
                shard.InsertInSlot(hash, ~slot, entry);
                if (HoldsIds && container.HoldsIds)
                    LogAdded(index, id, hash);
            }

            Index(container, id);
            return id;
        }

        /// <summary>
        /// Renames the entry of <paramref name="container"/>, whose content hash is <paramref name="hash"/>, to
        /// <paramref name="id"/> (<see cref="RenameEntry"/>), merging nothing: what a child's container pass does to an
        /// entry of its layer's own environment that an equal container's merge gave a smaller id it may own
        /// (<see cref="ContainerLayers.Insert{TContainer}"/>). Nothing when the entry has that id already. Thread-safe.
        /// </summary>
        /// <param name="container">The container.</param>
        /// <param name="hash">Its content hash.</param>
        /// <param name="id">The new id.</param>
        /// <returns><c>false</c> if the environment holds no equal container.</returns>
        internal bool RenameContent(in TContainer container, ulong hash, uint id)
        {
            Debug.Assert(hash == container.ComputeHash(), "the content hash is stale");

            var shard = toId[shardData.ShardOf(hash)];
            lock (shard)
            {
                var match = new ContainerContentMatch<TContainer>(container, hash);
                var slot = shard.Find(hash, ref match);
                if (slot < 0)
                    return false;

                ref var entry = ref shard.EntryAt(slot);
                if (entry.Id != id)
                    RenameEntry(ref entry, id);

                return true;
            }
        }

        /// <inheritdoc />
        internal override void RebuildLayer(ContainerLayers layers, ReadOnlySpan<uint> delta, ReadOnlySpan<uint> ownLosers, ContainerRebuildSummary summary) =>
            layers.RebuildType(this, delta, ownLosers, summary);

        /// <inheritdoc />
        internal override ContainerCatchUpClass ClassifyAdded(ContainerLayers layers, int depth, ContainerLogEntry entry, Rebuilder rebuilder) =>
            layers.ClassifyAdded(this, depth, entry, rebuilder);

        /// <inheritdoc />
        internal override void SettleCollision(ContainerLayers layers, int depth, ContainerLogEntry entry, ExecutionState state) =>
            layers.SettleCollision(this, depth, entry, state);

        /// <summary>
        /// Takes the entry of <paramref name="container"/>, whose content hash is <paramref name="hash"/>, out of the
        /// hash-cons, the reverse map and the element index, merging nothing and recording nothing: what a child's
        /// catch-up does to its layer's own entry of a content an ancestor gained under a smaller id, which the child
        /// then sees there (<see cref="ContainerLayers.SettleCollision{TContainer}"/>). Thread-safe.
        /// </summary>
        /// <param name="container">The container.</param>
        /// <param name="hash">Its content hash.</param>
        /// <param name="id">The id it was stored under, or zero.</param>
        /// <returns><c>false</c> if the environment holds no equal container.</returns>
        internal bool RemoveContent(in TContainer container, ulong hash, out uint id)
        {
            Debug.Assert(hash == container.ComputeHash(), "the content hash is stale");

            var shard = toId[shardData.ShardOf(hash)];
            lock (shard)
            {
                var match = new ContainerContentMatch<TContainer>(container, hash);
                if (shard.Remove(hash, ref match, out var gone) == false)
                {
                    id = 0;
                    return false;
                }

                id = gone.Id;
                var removed = RemoveById(gone.Container, gone.Hash, id);
                Debug.Assert(removed, "a hash-cons entry has no reverse-map entry");
            }

            Unindex(container, id);
            return true;
        }

        /// <summary>
        /// Gets the objects the environment takes its shard locks on for lookups, the hash-cons shards and then the
        /// reverse-map shards. A test hook: a test holds them to show which reads take no lock.
        /// </summary>
        internal object[] ShardLocks => [.. toId, .. byId];

        /// <summary>
        /// Probes a hash-cons shard for a container's id; the caller holds the shard or nothing writes it.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static bool TryFindId(RawHashTable<ContainerEntry<TContainer>> shard, in TContainer container, ulong hash, out uint id)
        {
            var match = new ContainerContentMatch<TContainer>(container, hash);
            var slot = shard.Find(hash, ref match);
            if (slot >= 0)
            {
                id = shard.EntryAt(slot).Id;
                return true;
            }

            id = 0;
            return false;
        }

        /// <summary>
        /// Probes a reverse-map shard for the container with an id; the caller holds the shard or nothing writes it.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static bool TryFindContainer(RawHashTable<ContainerEntry<TContainer>> shard, ulong hash, uint id, out TContainer container)
        {
            var match = new ContainerIdMatch<TContainer>(id);
            var slot = shard.Find(hash, ref match);
            if (slot >= 0)
            {
                container = shard.EntryAt(slot).Container;
                return true;
            }

            container = default;
            return false;
        }

    }

}

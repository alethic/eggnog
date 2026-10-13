using System.Collections.Generic;
using System.Diagnostics;

namespace Eggnog.Relations
{

    /// <summary>
    /// The per-entry step of a full container rebuild, run over one hash-cons shard by
    /// <see cref="RawHashTable{TEntry}.Retain{TPredicate}(ref TPredicate)"/>: the loop body of egglog's
    /// <c>apply_rebuild_nonincremental</c> and of phase one of its parallel variant
    /// (core-relations/src/containers/mod.rs:535-559, 593-624).
    /// </summary>
    /// <remarks>
    /// <para>
    /// For each entry: rebuild its id and its contents. Unchanged entries stay. An entry whose contents changed leaves
    /// the shard (the predicate returns <c>false</c>), its reverse-map entry and index occurrences are removed, and the
    /// rebuilt container is queued for reinsertion under the rebuilt id, in the outbox list of the hash-cons shard it
    /// will land in. An entry whose id alone changed is renamed in place (its content hash, and so its slot, stays),
    /// together with its reverse-map entry and its index occurrences, which egglog leaves stale (mod.rs:553-558,
    /// 618-623).
    /// </para>
    /// <para>
    /// A struct, so the shard walk is specialized and the container calls are direct; per entry it makes the two
    /// virtual calls into the rebuilder that egglog makes. The serial rebuild uses one outbox list; the parallel one
    /// gives each source shard a row of lists, one per destination shard, so phase two can insert per destination
    /// without locking the hash-cons (egglog's per-destination <c>SegQueue</c>s).
    /// </para>
    /// </remarks>
    /// <typeparam name="TContainer">The container type.</typeparam>
    internal struct ContainerRebuildPredicate<TContainer> : IEntryPredicate<ContainerEntry<TContainer>>
        where TContainer : struct, IContainerValue<TContainer>
    {

        readonly ContainerEnv<TContainer> env;
        readonly Rebuilder rebuilder;
        readonly List<ContainerReinsert<TContainer>>?[] outbox;
        readonly int outboxBase;
        readonly int destinations;
        bool changed;

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="env">The environment being rebuilt.</param>
        /// <param name="rebuilder">The rebuilder.</param>
        /// <param name="outbox">The reinsertion lists, <c>destinations</c> per source shard, created on demand.</param>
        /// <param name="outboxBase">The index of this source shard's first list.</param>
        /// <param name="destinations">The number of destination lists: one (serial) or the shard count.</param>
        public ContainerRebuildPredicate(ContainerEnv<TContainer> env, Rebuilder rebuilder, List<ContainerReinsert<TContainer>>?[] outbox, int outboxBase, int destinations)
        {
            this.env = env;
            this.rebuilder = rebuilder;
            this.outbox = outbox;
            this.outboxBase = outboxBase;
            this.destinations = destinations;
            changed = false;
        }

        /// <summary>
        /// Gets whether any entry changed.
        /// </summary>
        public readonly bool Changed => changed;

        /// <inheritdoc />
        public bool Keep(ref ContainerEntry<TContainer> entry)
        {
            var oldId = entry.Id;
            var newId = rebuilder.RebuildValue(oldId);
            var contentChanged = entry.Container.TryRebuild(rebuilder, out var rebuilt);
            if (contentChanged == false && newId == oldId)
                return true;

            changed = true;
            if (contentChanged)
            {
                // The contents changed: take the entry out of every structure and queue the rebuilt container.
                var removed = env.RemoveById(entry.Container, entry.Hash, oldId);
                Debug.Assert(removed, "a hash-cons entry has no reverse-map entry");
                env.Unindex(entry.Container, oldId);
                var hash = rebuilt.ComputeHash();
                var destination = outboxBase + (destinations == 1 ? 0 : env.ShardOf(hash));
                (outbox[destination] ??= new List<ContainerReinsert<TContainer>>()).Add(new ContainerReinsert<TContainer>(rebuilt, hash, newId, newId == oldId));
                return false;
            }

            // Only the id changed: leave the container in place under its new id.
            env.RenameEntry(ref entry, newId);
            return true;
        }

    }

}

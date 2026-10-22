using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Eggnog.Relations
{

    internal sealed partial class ContainerEnv<TContainer>
    {

        /// <inheritdoc />
        /// <remarks>
        /// <para>
        /// egglog's <c>apply_rebuild</c> (mod.rs:321-344): incremental when the provider has recent rows and
        /// <see cref="IsIncremental"/> holds for their count against the number of containers, otherwise full.
        /// </para>
        /// <para>
        /// A container's <see cref="IContainerValue{TSelf}.TryRebuild"/> or the merge function must not throw: the
        /// containers taken out for reinsertion would be lost (egglog panics at the same point).
        /// </para>
        /// </remarks>
        public override ContainerRebuildSummary ApplyRebuild(Table table, Rebuilder rebuilder, SubsetRef? recent, ExecutionState state)
        {
            ArgumentNullException.ThrowIfNull(table);
            ArgumentNullException.ThrowIfNull(rebuilder);
            ArgumentNullException.ThrowIfNull(state);

            // A type of base values only: none of its containers rebuilds, so egglog's walk over them changes nothing.
            if (HoldsIds == false)
                return new ContainerRebuildSummary();

            var count = Count;
            if (recent is { } subset && rebuilder.HintColumn is { } column && IsIncremental(subset.Size, count, parallelism.ParallelizeIntraContainerOp(count)))
                return ApplyRebuildIncremental(table, rebuilder, state, subset, column);

            return ApplyRebuildNonincremental(rebuilder, state);
        }

        /// <summary>
        /// Rebuilds only the containers that hold an id displaced in <paramref name="recent"/>, or are such an id.
        /// egglog's <c>apply_rebuild_incremental</c> (mod.rs:464-519), serial as egglog's is. Requires exclusive access.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The provider's rows in <paramref name="recent"/> are scanned for <paramref name="searchColumn"/> (for the
        /// union-find, column 0: the ids that stopped being canonical); each such id and the containers the element
        /// index lists for it are revisited, in that order without repeats. Each revisited container is taken out of
        /// every structure, rebuilt (contents and id) and inserted back, merging with an equal container if there is one
        /// (<see cref="ReinsertIncremental"/>).
        /// </para>
        /// <para>
        /// egglog takes out and reinserts one container at a time. Here every revisited container is taken out before
        /// any is reinserted, so a reinsertion can never merge into, and rename, a container that is still waiting to be
        /// rebuilt (which would hide it from its turn). With the exact element index, every container holding a
        /// displaced id is revisited, so after the pass no container mentions one.
        /// </para>
        /// <para>
        /// Cost: O(recent rows + revisited containers × size), plus one index update per value removed or added.
        /// </para>
        /// </remarks>
        /// <param name="table">The providing table.</param>
        /// <param name="rebuilder">Its rebuilder.</param>
        /// <param name="state">The state merge functions run in.</param>
        /// <param name="recent">The provider's rows appended since the last rebuild.</param>
        /// <param name="searchColumn">The provider column naming the displaced values.</param>
        /// <returns>What changed.</returns>
        internal ContainerRebuildSummary ApplyRebuildIncremental(Table table, Rebuilder rebuilder, ExecutionState state, SubsetRef recent, ColumnId searchColumn)
        {
            ArgumentNullException.ThrowIfNull(table);
            ArgumentNullException.ThrowIfNull(rebuilder);
            ArgumentNullException.ThrowIfNull(state);

            var summary = new ContainerRebuildSummary();
            var ids = scratchIds ??= new OrderedValueSet();
            var taken = scratchEntries ??= new List<ContainerEntry<TContainer>>();
            ids.Clear();
            taken.Clear();

            var collector = new DisplacedIdCollector(valIndex, ids);
            table.ForEachColumn(recent, searchColumn, ref collector);
            foreach (var id in ids.AsSpan())
                TakeAll(id, taken);

            foreach (ref var entry in CollectionsMarshal.AsSpan(taken))
            {
                var oldId = entry.Id;
                var rebuiltId = rebuilder.RebuildValue(oldId);
                if (entry.Container.TryRebuild(rebuilder, out var rebuilt))
                    ReinsertIncremental(rebuilt, rebuilt.ComputeHash(), oldId, rebuiltId, true, state, summary);
                else
                    ReinsertIncremental(entry.Container, entry.Hash, oldId, rebuiltId, false, state, summary);
            }

            ids.Clear();
            taken.Clear();
            return summary;
        }

        /// <summary>
        /// Inserts back a container an incremental rebuild took out and rebuilt, and records what changed. egglog's
        /// <c>reinsert_incremental</c> (mod.rs:441-462).
        /// </summary>
        /// <remarks>
        /// A change of contents or id is a change. The id is dirty only when the contents changed, the id did not, and
        /// the container kept that id through any merge: then rows mentioning the id mean something new without being
        /// rewritten. When the id changed, the ordinary table rebuild gives those rows a fresh timestamp anyway. The
        /// container must already be out of every structure (<see cref="TakeAll"/>), which is where egglog drops its
        /// reverse-map entry for a changed id.
        /// </remarks>
        /// <param name="container">The rebuilt container.</param>
        /// <param name="hash">Its content hash.</param>
        /// <param name="oldId">Its id before the rebuild.</param>
        /// <param name="rebuiltId">Its id rebuilt.</param>
        /// <param name="containerChanged">Whether the contents changed.</param>
        /// <param name="state">The state merge functions run in.</param>
        /// <param name="summary">Receives the changes.</param>
        internal void ReinsertIncremental(in TContainer container, ulong hash, uint oldId, uint rebuiltId, bool containerChanged, ExecutionState state, ContainerRebuildSummary summary)
        {
            if (containerChanged || rebuiltId != oldId)
                summary.NoteChange();

            var actual = InsertOwned(container, hash, rebuiltId, state);
            if (containerChanged && rebuiltId == oldId && actual == oldId)
                summary.NoteDirtyId(oldId);
        }

        /// <summary>
        /// Takes every container with id <paramref name="id"/> out of the reverse map, the hash-cons and the element
        /// index, appending them to <paramref name="taken"/>. Nothing happens if no container has the id (a displaced
        /// e-class id, say). Requires exclusive access.
        /// </summary>
        /// <param name="id">The id.</param>
        /// <param name="taken">Receives the entries taken out.</param>
        /// <returns>The number of containers taken out.</returns>
        internal int TakeAll(uint id, List<ContainerEntry<TContainer>> taken)
        {
            ArgumentNullException.ThrowIfNull(taken);

            var idHash = FxHasher.Hash(id);
            var idShard = byId[shardData.ShardOf(idHash)];
            var count = 0;
            while (true)
            {
                ContainerEntry<TContainer> entry;
                lock (idShard)
                {
                    var match = new ContainerIdMatch<TContainer>(id);
                    if (idShard.Remove(idHash, ref match, out entry) == false)
                        return count;
                }

                var shard = toId[shardData.ShardOf(entry.Hash)];
                lock (shard)
                {
                    var contentMatch = new ContainerContentMatch<TContainer>(entry.Container, entry.Hash);
                    var removed = shard.Remove(entry.Hash, ref contentMatch, out var gone);
                    Debug.Assert(removed && gone.Id == id, "a reverse-map entry has no hash-cons entry");
                }

                Unindex(entry.Container, id);
                taken.Add(entry);
                count++;
            }
        }

        /// <summary>
        /// Rebuilds every container. egglog's <c>apply_rebuild_nonincremental</c> (mod.rs:521-571). Requires exclusive
        /// access.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Goes parallel when <see cref="ParallelHeuristics.ParallelizeIntraContainerOp"/> holds for the number of
        /// containers. egglog gates this with <c>parallelize_inter_container_op</c>, the threshold for the number of
        /// environments (more than one), so there any two containers go parallel (mod.rs:526).
        /// </para>
        /// <para>
        /// Serially, each hash-cons shard is walked once (<see cref="ContainerRebuildPredicate{TContainer}"/>): an entry
        /// whose contents changed is taken out of every structure and queued; an entry whose id alone changed is renamed
        /// in place, moving its reverse-map entry and its index occurrences (egglog leaves the index stale here,
        /// mod.rs:553-558). The queued containers are then inserted back, merging where they collide; a container whose
        /// id did not change and survived its merge is dirty.
        /// </para>
        /// <para>
        /// Cost: O(total size of all containers), plus index updates for the changed ones.
        /// </para>
        /// </remarks>
        /// <param name="rebuilder">The rebuilder.</param>
        /// <param name="state">The state merge functions run in.</param>
        /// <returns>What changed.</returns>
        internal ContainerRebuildSummary ApplyRebuildNonincremental(Rebuilder rebuilder, ExecutionState state)
        {
            ArgumentNullException.ThrowIfNull(rebuilder);
            ArgumentNullException.ThrowIfNull(state);

            if (parallelism.ParallelizeIntraContainerOp(Count))
                return ApplyRebuildNonincrementalParallel(rebuilder, state);

            var summary = new ContainerRebuildSummary();
            var queue = scratchReinserts ??= new List<ContainerReinsert<TContainer>>();
            queue.Clear();
            var outbox = new List<ContainerReinsert<TContainer>>?[] { queue };
            foreach (var shard in toId)
            {
                lock (shard)
                {
                    var predicate = new ContainerRebuildPredicate<TContainer>(this, rebuilder, outbox, 0, 1);
                    shard.Retain(ref predicate);
                    if (predicate.Changed)
                        summary.NoteChange();
                }
            }

            foreach (var item in CollectionsMarshal.AsSpan(queue))
            {
                var actual = InsertOwned(item.Container, item.Hash, item.Id, state);

                // Refresh only when the rebuild changed a container's meaning in place. If the id changed, the ordinary
                // table rebuild already gives the parent rows a fresh timestamp (mod.rs:563-568).
                if (item.Stable && actual == item.Id)
                    summary.NoteDirtyId(item.Id);
            }

            queue.Clear();
            return summary;
        }

        /// <summary>
        /// Gets whether a rebuild after <paramref name="displaced"/> displaced ids should be incremental for an
        /// environment of <paramref name="containers"/> containers. egglog's <c>incremental_rebuild</c> (mod.rs:733-739):
        /// more than 1000 containers, and at most one displaced id per 8 containers, or per 512 when the full rebuild
        /// would run in parallel.
        /// </summary>
        /// <param name="displaced">The number of recent provider rows.</param>
        /// <param name="containers">The number of containers.</param>
        /// <param name="parallel">Whether a full rebuild would run in parallel.</param>
        /// <returns><c>true</c> to rebuild incrementally.</returns>
        internal static bool IsIncremental(long displaced, long containers, bool parallel)
        {
            return containers > 1000 && displaced * (parallel ? 512 : 8) <= containers;
        }

    }

}

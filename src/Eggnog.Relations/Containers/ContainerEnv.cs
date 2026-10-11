using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace Eggnog.Relations
{

    /// <summary>
    /// The hash-cons of one container type: egglog's <c>ContainerEnv&lt;C&gt;</c>
    /// (core-relations/src/containers/mod.rs:306-731). It interns containers to ids drawn from a database counter (the
    /// bridge passes its e-class id counter, so container ids share the id space and live in id columns and in the
    /// union-find), finds a container by its id, and rebuilds every container against the union-find, merging
    /// containers that become equal.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Three structures, each sharded with a lock per shard (egglog's three <c>DashMap</c>s):
    /// </para>
    /// <list type="bullet">
    /// <item><description>the hash-cons, container to id (egglog's <c>to_id</c>), keyed by content hash;</description></item>
    /// <item><description>the reverse map, id to container (egglog's <c>to_container</c>), keyed by the id's hash. It
    /// holds the container itself rather than egglog's (hash, shard) pair, so a lookup by id takes one lock and one probe
    /// where egglog takes the same read lock twice (mod.rs:704-706; map §8.1 row 14);</description></item>
    /// <item><description>the element index, value to the ids of containers holding it (egglog's <c>val_index</c>),
    /// kept exact (<see cref="ContainerValueIndex"/>; map §8.1 row 13).</description></item>
    /// </list>
    /// <para>
    /// Invariants, outside a rebuild in progress: the reverse map holds exactly one entry per hash-cons entry, with the
    /// same container, hash and id; the element index holds exactly the occurrences of the values of the hash-cons
    /// entries under their ids (<see cref="CheckInvariants"/>). Content keys are unique; ids normally are too (a
    /// bijection), but a rebuild that renames one container's id to another container's id makes two entries share it,
    /// as in egglog; lookups by id then return either.
    /// </para>
    /// <para>
    /// Threading: <see cref="GetOrInsert"/>, <see cref="TryGetId"/>, <see cref="TryGetContainer"/> and
    /// <see cref="ForEach{TVisitor}"/> may run on any number of threads at once (egglog's <c>&amp;self</c> methods, called
    /// from parallel rule actions). Rebuilding needs exclusive access (egglog's <c>&amp;mut self</c>). Locks are always
    /// taken hash-cons shard first, then a reverse-map or index shard, never the other way round.
    /// </para>
    /// <para>
    /// Containers are immutable (<see cref="IContainerValue{TSelf}"/>), so a container read back may be kept, and a
    /// clone of the environment shares them.
    /// </para>
    /// </remarks>
    /// <typeparam name="TContainer">The container type; a struct, so all of this is specialized for it.</typeparam>
    internal sealed partial class ContainerEnv<TContainer> : ContainerEnvBase
        where TContainer : struct, IContainerValue<TContainer>
    {

        readonly ContainerMergeFunction merge;
        readonly CounterId counter;
        readonly ParallelHeuristics parallelism;
        readonly ShardData shardData;
        readonly RawHashTable<ContainerEntry<TContainer>>[] toId;
        readonly RawHashTable<ContainerEntry<TContainer>>[] byId;
        readonly ContainerValueIndex valIndex;

        /// <summary>
        /// Scratch for the ids an incremental rebuild revisits; used only under exclusive access.
        /// </summary>
        OrderedValueSet? scratchIds;

        /// <summary>
        /// Scratch for the entries a rebuild takes out; used only under exclusive access.
        /// </summary>
        List<ContainerEntry<TContainer>>? scratchEntries;

        /// <summary>
        /// Scratch for the containers a serial full rebuild reinserts; used only under exclusive access.
        /// </summary>
        List<ContainerReinsert<TContainer>>? scratchReinserts;

        /// <summary>
        /// Initializes an empty environment. egglog's <c>ContainerEnv::new</c>.
        /// </summary>
        /// <param name="typeId">The environment's id in its <see cref="ContainerValues"/>.</param>
        /// <param name="merge">Resolves the ids of containers that become equal.</param>
        /// <param name="counter">The counter fresh container ids come from.</param>
        /// <param name="parallelism">When rebuilds go parallel; also fixes the shard count.</param>
        public ContainerEnv(ContainerTypeId typeId, ContainerMergeFunction merge, CounterId counter, ParallelHeuristics parallelism)
            : base(typeId)
        {
            ArgumentNullException.ThrowIfNull(merge);
            ArgumentNullException.ThrowIfNull(parallelism);
            ArgumentOutOfRangeException.ThrowIfNegative(counter.Rep, nameof(counter));

            this.merge = merge;
            this.counter = counter;
            this.parallelism = parallelism;

            // DashMap's default: four shards per thread, a power of two; one when nothing runs in parallel.
            var dop = parallelism.DegreeOfParallelism;
            shardData = new ShardData(dop <= 1 ? 1 : dop * 4);
            toId = NewShards(shardData);
            byId = NewShards(shardData);
            valIndex = new ContainerValueIndex(shardData);
        }

        /// <summary>
        /// Initializes a copy, for <see cref="Clone"/>.
        /// </summary>
        ContainerEnv(ContainerEnv<TContainer> source)
            : base(source.TypeId)
        {
            merge = source.merge;
            counter = source.counter;
            parallelism = source.parallelism;
            shardData = source.shardData;
            toId = CloneShards(source.toId);
            byId = CloneShards(source.byId);
            valIndex = source.valIndex.Clone();
        }

        /// <inheritdoc />
        public override Type ContainerType => typeof(TContainer);

        /// <summary>
        /// Gets the merge function that resolves the ids of containers that become equal.
        /// </summary>
        public ContainerMergeFunction MergeFunction => merge;

        /// <summary>
        /// Gets the counter fresh container ids come from.
        /// </summary>
        public CounterId Counter => counter;

        /// <summary>
        /// Gets the element index. For tests and invariant checks.
        /// </summary>
        internal ContainerValueIndex ValueIndex => valIndex;

        /// <inheritdoc />
        /// <remarks>Reads each shard's count without locking: exact under exclusive access, a snapshot otherwise.</remarks>
        public override int Count
        {
            get
            {
                var count = 0;
                foreach (var shard in toId)
                    count += shard.Count;

                return count;
            }
        }

        /// <summary>
        /// Returns the id of <paramref name="container"/>, interning it with a fresh id from <see cref="Counter"/> if it
        /// is new. egglog's <c>get_or_insert</c> (mod.rs:366-409). Thread-safe: equal containers interned concurrently
        /// get one id.
        /// </summary>
        /// <remarks>
        /// <para>
        /// egglog looks the container up under a read lock, mints an id, publishes it in the reverse map, then inserts
        /// under the write lock and, if another thread won the race, withdraws its id, leaving a gap in the counter. Here
        /// the lookup and the insert happen under one shard lock, so only the winner mints an id and racing interns waste
        /// none. As in egglog, the reverse map learns the id before the hash-cons publishes it, so any thread that obtains
        /// the id can look the container up; the element index is updated after the lock is released.
        /// </para>
        /// <para>
        /// Cost: one content hash, one lock, one expected-O(1) probe; for a new container also an id increment, a
        /// reverse-map insert and one index update per value.
        /// </para>
        /// </remarks>
        /// <param name="container">The container; it must not change afterwards.</param>
        /// <param name="state">The execution state, which mints the id; it must belong to a database.</param>
        /// <returns>The container's id.</returns>
        /// <exception cref="InvalidOperationException">The state belongs to no database, or the counter ran out of
        /// ids.</exception>
        public uint GetOrInsert(in TContainer container, ExecutionState state)
        {
            ArgumentNullException.ThrowIfNull(state);

            var hash = container.ComputeHash();
            var shard = toId[shardData.ShardOf(hash)];
            uint id;
            lock (shard)
            {
                var match = new ContainerContentMatch<TContainer>(container, hash);
                var slot = shard.FindOrFindInsertSlot(hash, ref match, default(ContainerContentHasher<TContainer>));
                if (slot >= 0)
                    return shard.EntryAt(slot).Id;

                id = MintId(state);
                var entry = new ContainerEntry<TContainer>(container, hash, id);
                AddById(entry);
                shard.InsertInSlot(hash, ~slot, entry);
            }

            Index(container, id);
            return id;
        }

        /// <summary>
        /// Gets the id of <paramref name="container"/> without interning it. Thread-safe; one lock and one probe.
        /// </summary>
        /// <param name="container">The container.</param>
        /// <param name="id">Its id, or zero.</param>
        /// <returns><c>true</c> if the container is interned.</returns>
        public bool TryGetId(in TContainer container, out uint id)
        {
            var hash = container.ComputeHash();
            var shard = toId[shardData.ShardOf(hash)];
            lock (shard)
            {
                var match = new ContainerContentMatch<TContainer>(container, hash);
                var slot = shard.Find(hash, ref match);
                if (slot >= 0)
                {
                    id = shard.EntryAt(slot).Id;
                    return true;
                }
            }

            id = 0;
            return false;
        }

        /// <summary>
        /// Gets the container with id <paramref name="id"/>. egglog's <c>get_container</c> (mod.rs:701-730), which
        /// returns a guard holding a shard read lock and takes that lock twice (map §8.1 row 14). Here the lookup takes
        /// one lock once and returns the immutable container, so the caller holds no lock afterwards and may keep it.
        /// Thread-safe.
        /// </summary>
        /// <param name="id">The container id.</param>
        /// <param name="container">The container, or <c>default</c>.</param>
        /// <returns><c>false</c> if no container of this type has the id (for instance an id that a rebuild merged
        /// away, until the tables canonicalize it).</returns>
        public bool TryGetContainer(uint id, out TContainer container)
        {
            var hash = FxHasher.Hash(id);
            var shard = byId[shardData.ShardOf(hash)];
            lock (shard)
            {
                var match = new ContainerIdMatch<TContainer>(id);
                var slot = shard.Find(hash, ref match);
                if (slot >= 0)
                {
                    container = shard.EntryAt(slot).Container;
                    return true;
                }
            }

            container = default;
            return false;
        }

        /// <summary>
        /// Visits every container with its id, a shard at a time under that shard's lock. egglog's
        /// <c>ContainerValues::for_each</c> (mod.rs:135-143). The visitor must not call back into this environment.
        /// The order is the hash-cons's slot order, deterministic for a given history.
        /// </summary>
        /// <typeparam name="TVisitor">The visitor type.</typeparam>
        /// <param name="visitor">Receives each container and id.</param>
        public void ForEach<TVisitor>(ref TVisitor visitor)
            where TVisitor : struct, IContainerVisitor<TContainer>
        {
            foreach (var shard in toId)
            {
                lock (shard)
                {
                    for (var slot = shard.NextFullSlot(0); slot >= 0; slot = shard.NextFullSlot(slot + 1))
                    {
                        ref var entry = ref shard.EntryAt(slot);
                        visitor.Visit(in entry.Container, entry.Id);
                    }
                }
            }
        }

        /// <summary>
        /// Inserts <paramref name="container"/> under <paramref name="value"/>, or, if an equal container is present,
        /// merges the two ids with the merge function and keeps the result. egglog's <c>insert_owned</c>
        /// (mod.rs:411-439). Used by rebuilds, after the container was taken out of every structure.
        /// </summary>
        /// <param name="container">The container.</param>
        /// <param name="value">The id to insert it under.</param>
        /// <param name="state">The state the merge function runs in.</param>
        /// <returns>The id the container ends up with: <paramref name="value"/>, or the merge's result.</returns>
        internal uint InsertOwned(in TContainer container, uint value, ExecutionState state) => InsertOwned(container, container.ComputeHash(), value, state);

        /// <summary>
        /// <see cref="InsertOwned(in TContainer, uint, ExecutionState)"/> with the content hash already known.
        /// </summary>
        /// <param name="container">The container.</param>
        /// <param name="hash">Its content hash.</param>
        /// <param name="value">The id to insert it under.</param>
        /// <param name="state">The state the merge function runs in.</param>
        /// <returns>The id the container ends up with.</returns>
        internal uint InsertOwned(in TContainer container, ulong hash, uint value, ExecutionState state)
        {
            Debug.Assert(hash == container.ComputeHash(), "the content hash is stale");

            var shard = toId[shardData.ShardOf(hash)];
            lock (shard)
            {
                var match = new ContainerContentMatch<TContainer>(container, hash);
                var slot = shard.FindOrFindInsertSlot(hash, ref match, default(ContainerContentHasher<TContainer>));
                if (slot >= 0)
                {
                    // An equal container is present: its entry keeps the merged id. The incoming id was taken out of
                    // every structure with its container, so nothing else refers to it.
                    ref var entry = ref shard.EntryAt(slot);
                    var current = entry.Id;
                    var result = merge.Merge(state, current, value);
                    if (result != current)
                        RenameEntry(ref entry, result);

                    return result;
                }

                var fresh = new ContainerEntry<TContainer>(container, hash, value);
                AddById(fresh);
                shard.InsertInSlot(hash, ~slot, fresh);
            }

            Index(container, value);
            return value;
        }

        /// <inheritdoc />
        public override void ExtendContainersContaining(ReadOnlySpan<uint> values, OrderedValueSet output)
        {
            ArgumentNullException.ThrowIfNull(output);

            foreach (var value in values)
                valIndex.CollectContaining(value, output);
        }

        /// <inheritdoc />
        public override ContainerEnvBase Clone() => new ContainerEnv<TContainer>(this);

        /// <summary>
        /// Gets the shard of the hash-cons a content hash belongs to.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal int ShardOf(ulong hash) => shardData.ShardOf(hash);

        /// <summary>
        /// Renames a hash-cons entry to <paramref name="newId"/>: moves its reverse-map entry and its occurrences in the
        /// element index, then updates the entry in place. The caller holds the entry's hash-cons shard (by lock or by
        /// exclusive access).
        /// </summary>
        /// <param name="entry">The entry, in its hash-cons shard.</param>
        /// <param name="newId">The new id.</param>
        internal void RenameEntry(ref ContainerEntry<TContainer> entry, uint newId)
        {
            var oldId = entry.Id;
            var removed = RemoveById(entry.Container, entry.Hash, oldId);
            Debug.Assert(removed, "a hash-cons entry has no reverse-map entry");

            var renamed = new ContainerEntry<TContainer>(entry.Container, entry.Hash, newId);
            AddById(renamed);
            var renamer = new ValueIndexRenamer(valIndex, oldId, newId);
            entry.Container.ForEachValue(ref renamer);
            entry.Id = newId;
        }

        /// <summary>
        /// Adds a reverse-map entry.
        /// </summary>
        internal void AddById(in ContainerEntry<TContainer> entry)
        {
            var hash = FxHasher.Hash(entry.Id);
            var shard = byId[shardData.ShardOf(hash)];
            lock (shard)
                shard.InsertUnique(hash, entry, default(ContainerIdHasher<TContainer>));
        }

        /// <summary>
        /// Removes the reverse-map entry of one particular container under one id.
        /// </summary>
        /// <returns><c>true</c> if it was present.</returns>
        internal bool RemoveById(in TContainer container, ulong contentHash, uint id)
        {
            var hash = FxHasher.Hash(id);
            var shard = byId[shardData.ShardOf(hash)];
            lock (shard)
            {
                var match = new ContainerExactMatch<TContainer>(container, contentHash, id);
                return shard.Remove(hash, ref match, out _);
            }
        }

        /// <summary>
        /// Records the occurrences of the values of <paramref name="container"/> under <paramref name="id"/>.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal void Index(in TContainer container, uint id)
        {
            var adder = new ValueIndexAdder(valIndex, id);
            container.ForEachValue(ref adder);
        }

        /// <summary>
        /// Removes the occurrences of the values of <paramref name="container"/> recorded under <paramref name="id"/>.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal void Unindex(in TContainer container, uint id)
        {
            var remover = new ValueIndexRemover(valIndex, id);
            container.ForEachValue(ref remover);
        }

        /// <summary>
        /// Mints a fresh id from the counter: egglog's <c>Value::from_usize(exec_state.inc_counter(counter))</c>.
        /// </summary>
        uint MintId(ExecutionState state)
        {
            var raw = state.IncrementCounter(counter);
            if ((ulong)raw >= Value.StaleRep)
                ThrowExhausted();

            return (uint)raw;
        }

        [DoesNotReturn]
        [MethodImpl(MethodImplOptions.NoInlining)]
        void ThrowExhausted()
        {
            throw new InvalidOperationException($"Counter {counter} has no ids left for containers of type {typeof(TContainer).Name}.");
        }

        static RawHashTable<ContainerEntry<TContainer>>[] NewShards(ShardData shardData)
        {
            var shards = new RawHashTable<ContainerEntry<TContainer>>[shardData.Count];
            for (var i = 0; i < shards.Length; i++)
                shards[i] = new RawHashTable<ContainerEntry<TContainer>>();

            return shards;
        }

        static RawHashTable<ContainerEntry<TContainer>>[] CloneShards(RawHashTable<ContainerEntry<TContainer>>[] source)
        {
            var shards = new RawHashTable<ContainerEntry<TContainer>>[source.Length];
            for (var i = 0; i < shards.Length; i++)
                shards[i] = source[i].Clone();

            return shards;
        }

    }

}

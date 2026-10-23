using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading;

namespace Eggnog.Relations
{

    /// <summary>
    /// One child context's containers of classes kept apart from its ancestors': per container type, a layer
    /// (<see cref="ContainerLayer{TContainer}"/>) of the containers the context holds itself and the ancestor ids it
    /// shadows. The context and its descendants read containers through a view (<see cref="ContainerView"/>): the
    /// layers of the context and its ancestors, nearest first, over the root's environments.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Levels. The reader is level d, its ancestors' layers are levels d - 1 down to 1, and the root's environments
    /// (<see cref="ContainerValues"/>, one per container type, shared by every context) are level 0. A layer's own
    /// environment holds an entry under an id only when the id is local to its context (minted from the context's id
    /// blocks, <see cref="IdBlockAllocator.Contains"/>) or shadowed by the layer
    /// (<see cref="ContainerLayerBase.Shadowed"/>); a shadowed id hides every entry under it at the levels below.
    /// </para>
    /// <para>
    /// Lookups. By id (<see cref="TryGetContainer{TContainer}"/>), each level from d down to 1 is skipped without being
    /// touched unless its layer of the type exists and owns the id, by the lock-free ownership test; the first level
    /// that owns it answers, a miss there being a miss (the id was merged away); with none, the root answers. By content
    /// (<see cref="TryGetId{TContainer}"/>), the content is hashed once, every level with a non-empty layer of the type
    /// is probed from d down, then the root, and a hit at level L counts only if no level above L, up to d, shadows its
    /// id.
    /// </para>
    /// <para>
    /// Locks. A frozen level's layers are read without locks: nothing writes them after the volatile write of its
    /// frozen flag. A writable level's are read under their shard locks: the reader's own, written by its rule tasks,
    /// and a writable ancestor's, idle while a descendant runs but read from a callback of its own run too; those locks
    /// are uncontended, since one thread domain holds every writable level. Level 0 is always read under its locks: the
    /// root's environments take containers of values from any context and thread
    /// (<see cref="ContainerEnv{TContainer}.GetOrInsertShared"/>), and a shard resize writes its arrays one after the
    /// other.
    /// </para>
    /// <para>
    /// Threads. The slots, one per container type by <see cref="ContainerTypeId"/>, are published copy-on-write with an
    /// interlocked compare-exchange, since rule tasks may create layers while others read; a slot is set once. The
    /// shadowed ids change only on the context's domain thread, and nothing changes once the context is frozen
    /// (<see cref="Freeze"/>).
    /// </para>
    /// <para>
    /// Log. The context's container log (<see cref="Log"/>) records, while the context has live descendants, every
    /// container of classes that appears in one of its layers' own environments, which those descendants read through
    /// their cursors into it, as they read the root's from the store's (<see cref="Store.ContainerLog"/>).
    /// </para>
    /// <para>
    /// The engine places nothing in a context's layers: every layer is empty unless a test placed a container there
    /// (<see cref="Place{TContainer}"/>), so a child reads the root's containers.
    /// </para>
    /// </remarks>
    internal sealed partial class ContainerLayers
    {

        readonly ContainerLayers? parent;
        readonly IdBlockAllocator idBlocks;
        readonly ParallelHeuristics parallelism;

        /// <summary>
        /// The layers by container type id; an index past the end, or a <c>null</c> slot, means no layer. Never written
        /// after publication: a new layer publishes a copy with an interlocked compare-exchange.
        /// </summary>
        ContainerLayerBase?[] slots = Array.Empty<ContainerLayerBase?>();

        /// <summary>
        /// Whether the context is frozen and its layers read without locks; set last by <see cref="Freeze"/>.
        /// </summary>
        volatile bool frozen;

        /// <summary>
        /// Initializes the empty layers of a child context.
        /// </summary>
        /// <param name="parent">The parent's layers; <c>null</c> when the parent is the root.</param>
        /// <param name="idBlocks">The child's id blocks, which decide what is local to it.</param>
        /// <param name="parallelism">The child's parallelism, which fixes the shard count of its own environments.</param>
        public ContainerLayers(ContainerLayers? parent, IdBlockAllocator idBlocks, ParallelHeuristics parallelism)
        {
            ArgumentNullException.ThrowIfNull(idBlocks);
            ArgumentNullException.ThrowIfNull(parallelism);
            this.parent = parent;
            this.idBlocks = idBlocks;
            this.parallelism = parallelism;
        }

        /// <summary>
        /// Gets the parent's layers, or <c>null</c> when the parent is the root.
        /// </summary>
        public ContainerLayers? Parent => parent;

        /// <summary>
        /// Gets the context's container log: every own environment of its layers records into it, while it is active,
        /// each container of classes that appears there (<see cref="ContainerLog"/>).
        /// </summary>
        public ContainerLog Log { get; } = new();

        /// <summary>
        /// Gets whether the context is frozen, and its layers read without locks. One volatile read.
        /// </summary>
        public bool IsFrozen => frozen;

        /// <summary>
        /// Gets whether the context has no layer of any type. One volatile read and a pass over the slots, one per
        /// registered container type at most.
        /// </summary>
        public bool IsEmpty
        {
            get
            {
                foreach (var slot in Volatile.Read(ref slots))
                    if (slot is not null)
                        return false;

                return true;
            }
        }

        /// <summary>
        /// Gets whether <paramref name="id"/> is local to the context: minted from its id blocks. Lock-free,
        /// O(log blocks).
        /// </summary>
        /// <param name="id">The id.</param>
        /// <returns><c>true</c> if the id is local.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool IsLocal(uint id) => idBlocks.Contains(id);

        /// <summary>
        /// Gets whether the context's layer <paramref name="layer"/> may hold an entry under <paramref name="id"/>: the id
        /// is local to the context or the layer shadows it (the ownership rule). Lock-free: a binary search over the id
        /// blocks and a probe of the shadowed ids.
        /// </summary>
        /// <param name="layer">One of the context's layers.</param>
        /// <param name="id">The id.</param>
        /// <returns><c>true</c> if the layer owns the id.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool Owns(ContainerLayerBase layer, uint id) => idBlocks.Contains(id) || layer.Shadowed.Contains(id);

        /// <summary>
        /// Gets the context's layer of the container type with id <paramref name="type"/>, or <c>null</c>. Lock-free:
        /// one volatile read and one array load.
        /// </summary>
        /// <param name="type">The container type's id.</param>
        /// <returns>The layer, or <c>null</c>.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ContainerLayerBase? LayerAt(ContainerTypeId type)
        {
            var current = Volatile.Read(ref slots);
            return (uint)type.Index < (uint)current.Length ? current[type.Index] : null;
        }

        /// <summary>
        /// Gets the context's layer of the type <paramref name="root"/> holds, or <c>null</c>. Lock-free.
        /// </summary>
        /// <typeparam name="TContainer">The container type.</typeparam>
        /// <param name="root">The root's environment of the type.</param>
        /// <returns>The layer, or <c>null</c>.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ContainerLayer<TContainer>? LayerOf<TContainer>(ContainerEnv<TContainer> root)
            where TContainer : struct, IContainerValue<TContainer>
        {
            return Unsafe.As<ContainerLayer<TContainer>?>(LayerAt(root.TypeId));
        }

        /// <summary>
        /// Gets the context's layer of the type <paramref name="root"/> holds, creating it, with an empty own
        /// environment that records into the context's container log (<see cref="Log"/>), on first use. Thread-safe:
        /// rule tasks of the context may race here and get one layer, the one whose copy of the slots was published
        /// first. The slots are published without a lock; creating a layer takes the log's lock once, to attach the new
        /// environment, and a task whose copy lost the race takes it once more, to detach its own.
        /// </summary>
        /// <typeparam name="TContainer">The container type.</typeparam>
        /// <param name="root">The root's environment of the type.</param>
        /// <returns>The layer.</returns>
        /// <exception cref="InvalidOperationException">The context is frozen.</exception>
        public ContainerLayer<TContainer> GetOrCreateLayer<TContainer>(ContainerEnv<TContainer> root)
            where TContainer : struct, IContainerValue<TContainer>
        {
            ArgumentNullException.ThrowIfNull(root);
            var index = root.TypeId.Index;
            ContainerLayer<TContainer>? layer = null;
            while (true)
            {
                var current = Volatile.Read(ref slots);
                if (index < current.Length && current[index] is { } existing)
                {
                    // A layer that lost the race was never published: nothing recorded into its environment.
                    if (layer is not null)
                        Log.Detach(layer.OwnBase);

                    return Unsafe.As<ContainerLayer<TContainer>>(existing);
                }

                // The context freezes on its domain thread while none of its rule tasks runs.
                if (frozen)
                    throw new InvalidOperationException("A frozen context's container layers never change.");

                // The own environment gets the log before the layer is published, so every task that finds the layer
                // records into it.
                if (layer is null)
                {
                    layer = new ContainerLayer<TContainer>(root, parallelism);
                    Log.Attach(layer.OwnBase);
                }

                var grown = new ContainerLayerBase?[Math.Max(current.Length, index + 1)];
                Array.Copy(current, grown, current.Length);
                grown[index] = layer;

                // Release: a reader that sees the new array sees the layer fully built. A task that lost the race to
                // another type's layer retries over the winner's copy; one that lost it to this type's returns the
                // winner's layer above.
                if (ReferenceEquals(Interlocked.CompareExchange(ref slots, grown, current), current))
                    return layer;
            }
        }

        /// <summary>
        /// Places <paramref name="container"/> under <paramref name="id"/> in the context's layer of its type, shadowing
        /// the id when it is not local to the context, so that the context and its descendants read the container under
        /// the id, and no ancestor's entry under it. A test hook: nothing in the engine places containers in a layer;
        /// tests do, to read a child's view of containers it holds itself. Domain thread only.
        /// </summary>
        /// <typeparam name="TContainer">The container type.</typeparam>
        /// <param name="root">The root's environment of the type.</param>
        /// <param name="container">The container; it must not change afterwards.</param>
        /// <param name="id">The id: local to the context, or an ancestor's.</param>
        /// <exception cref="InvalidOperationException">The context is frozen, or its layer of the type holds an equal
        /// container already; the view is then unchanged.</exception>
        internal void Place<TContainer>(ContainerEnv<TContainer> root, in TContainer container, uint id)
            where TContainer : struct, IContainerValue<TContainer>
        {
            if (frozen)
                throw new InvalidOperationException("A frozen context's container layers never change.");

            // Refused before the id is shadowed, since a shadowed id stays so: shadowing it would hide every ancestor's
            // entry under it although nothing is placed. Only the domain thread places, so the test and the insert agree.
            var layer = GetOrCreateLayer(root);
            var hash = container.ComputeHash();
            if (layer.Own.TryGetId(container, hash, out _))
                throw new InvalidOperationException($"The context's layer of {typeof(TContainer).Name} holds an equal container already.");

            if (IsLocal(id) == false)
                layer.Shadowed.Add(id);

            var inserted = layer.Own.TryInsertNew(container, hash, id);
            Debug.Assert(inserted, "only the domain thread places containers in a layer");
        }

    }

}

using System;
using System.Collections.Generic;

namespace Eggnog.Relations
{

    /// <summary>
    /// One child context's layer of one container type: an own environment of the containers the context holds itself,
    /// and the ancestor ids it shadows (<see cref="ContainerLayers"/>). The context reads the type through its view
    /// (<see cref="ContainerView"/>): its own and its ancestors' layers over the root's environment, an ancestor's entry
    /// hidden wherever a level above it shadows the entry's id.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Ownership. <see cref="Own"/> holds an entry under an id only when the id is local to the context (minted from its
    /// id blocks, <see cref="IdBlockAllocator.Contains"/>) or shadowed here (<see cref="ContainerLayerBase.Shadowed"/>):
    /// a lookup by id then probes the one level that can own the id and skips every other with two lock-free tests.
    /// </para>
    /// <para>
    /// Threads. <see cref="ContainerLayers"/> creates the layer on first use, and the own environment with it, from the
    /// root environment's merge function and counter and the context's parallelism, and publishes it with an interlocked
    /// compare-exchange; the context's rule tasks may then insert into it concurrently under its shard locks. Once the context is frozen nothing writes the layer,
    /// and readers on any thread read it without locks.
    /// </para>
    /// </remarks>
    /// <typeparam name="TContainer">The container type.</typeparam>
    internal sealed class ContainerLayer<TContainer> : ContainerLayerBase
        where TContainer : struct, IContainerValue<TContainer>
    {

        /// <summary>
        /// The number of containers <see cref="Own"/> held when the layer froze; -1 before.
        /// </summary>
        int frozenCount = -1;

        /// <summary>
        /// Initializes an empty layer of the type <paramref name="root"/> holds.
        /// </summary>
        /// <param name="root">The root's environment of the type.</param>
        /// <param name="parallelism">The context's parallelism, which fixes the own environment's shard count.</param>
        public ContainerLayer(ContainerEnv<TContainer> root, ParallelHeuristics parallelism)
            : base(root.TypeId)
        {
            ArgumentNullException.ThrowIfNull(root);
            Own = new ContainerEnv<TContainer>(root.TypeId, root.MergeFunction, root.Counter, parallelism, holdsIds: true);
        }

        /// <summary>
        /// Gets the layer's own environment: the containers the context holds itself, under its local ids and the ids it
        /// shadows.
        /// </summary>
        public ContainerEnv<TContainer> Own { get; }

        /// <inheritdoc />
        public override ContainerEnvBase OwnBase => Own;

        /// <inheritdoc />
        public override bool IsEmpty => Own.Count == 0;

        /// <summary>
        /// Gets the container the layer's own environment holds under <paramref name="id"/>, taking its shard lock
        /// unless <paramref name="frozen"/>.
        /// </summary>
        /// <param name="id">The id.</param>
        /// <param name="frozen">Whether the owning context is frozen.</param>
        /// <param name="container">The container, or <c>default</c>.</param>
        /// <returns><c>true</c> if the layer holds one under the id.</returns>
        public bool TryGetContainer(uint id, bool frozen, out TContainer container) =>
            frozen ? Own.TryGetContainerUnlocked(id, out container) : Own.TryGetContainer(id, out container);

        /// <summary>
        /// Gets the id the layer's own environment holds <paramref name="container"/> under, taking its shard lock unless
        /// <paramref name="frozen"/>.
        /// </summary>
        /// <param name="container">The container.</param>
        /// <param name="hash">Its content hash.</param>
        /// <param name="frozen">Whether the owning context is frozen.</param>
        /// <param name="id">The id, or zero.</param>
        /// <returns><c>true</c> if the layer holds the container.</returns>
        public bool TryGetId(in TContainer container, ulong hash, bool frozen, out uint id) =>
            frozen ? Own.TryGetIdUnlocked(container, hash, out id) : Own.TryGetId(container, hash, out id);

        /// <inheritdoc />
        public override void Freeze()
        {
            Shadowed.Freeze();
            frozenCount = Own.Count;
        }

        /// <inheritdoc />
        public override void CheckInvariants(ContainerLayers owner, List<string> violations)
        {
            ArgumentNullException.ThrowIfNull(owner);
            ArgumentNullException.ThrowIfNull(violations);

            try
            {
                Own.CheckInvariants();
            }
            catch (InvalidOperationException e)
            {
                Context.Report(violations, $"container layers: type {TypeId}: {e.Message}");
            }

            var checker = new ContainerOwnershipChecker<TContainer>(owner, Shadowed, TypeId, violations);
            Own.ForEach(ref checker);

            var shadowed = new List<uint>();
            Shadowed.CopyTo(shadowed);
            foreach (var id in shadowed)
                if (owner.IsLocal(id))
                    Context.Report(violations, $"container layers: type {TypeId} shadows {id}, which is local to the context");

            if (frozenCount >= 0 && Own.Count != frozenCount)
                Context.Report(violations, $"container layers: type {TypeId} held {frozenCount} containers when the context froze and holds {Own.Count} now");
        }

    }

}

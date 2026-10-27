using System.Diagnostics;

namespace Eggnog.Relations
{

    internal sealed partial class ContainerLayers
    {

        /// <summary>
        /// Returns the id of <paramref name="container"/>, a container of classes, in the view of this context, interning
        /// it in the context's own layer of its type when the view does not hold it: egglog's <c>get_or_insert</c>
        /// (core-relations/src/containers/mod.rs:366-409) in the copy its push makes, with the view standing for that
        /// copy's one hash-cons. Rule actions, external functions and the library's encodes of a writable child intern
        /// through here (<see cref="ContainerEnv{TContainer}.GetOrInsert"/>, <see cref="ContainerView.GetOrInsert{TContainer}(ContainerEnv{TContainer}, in TContainer)"/>).
        /// Thread-safe.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The content is looked up through the view (<see cref="TryGetId{TContainer}"/>); on a miss the layer is made on
        /// first use, and its own environment probes again under the content's shard lock, mints an id from the
        /// context's id blocks and inserts (<see cref="ContainerEnv{TContainer}.GetOrInsertLocal"/>), recording the
        /// entry in the context's container log while its descendants live. The context's rule tasks that race to
        /// intern one content agree on one id. The ancestors never see the container, so disposing the context drops
        /// it with its layers (<see cref="Release"/>); its id stays local to the context, so a lift drops the rows that
        /// name it (<see cref="MemberMap.Members"/>).
        /// </para>
        /// <para>
        /// The ancestors hold still meanwhile: a frozen one never changes, and a writable one does not run while its
        /// descendant does. When a writable ancestor later gains an equal content under another id, the context's
        /// catch-up merges the two, the smaller id winning, as egglog's one hash-cons would have kept the first
        /// (<see cref="SettleCollision{TContainer}"/>). Interning is immediate and never rolled back, as in egglog: a
        /// container interned by a rule whose other writes fail stays.
        /// </para>
        /// <para>
        /// Cost: the view's lookup by content, one probe per level with a non-empty layer of the type plus the root's;
        /// for a new container also one locked probe, an id from the current block and an insert into the own
        /// environment, and one index update per value.
        /// </para>
        /// </remarks>
        /// <typeparam name="TContainer">The container type.</typeparam>
        /// <param name="root">The root's environment of the type, which holds ids.</param>
        /// <param name="container">The container; it must not change afterwards.</param>
        /// <returns>The container's id in the view.</returns>
        /// <exception cref="System.InvalidOperationException">The container is new in the view and the context is frozen:
        /// a frozen context is read-only.</exception>
        /// <exception cref="System.OverflowException">The store has no ids left for a new block.</exception>
        public uint GetOrInsert<TContainer>(ContainerEnv<TContainer> root, in TContainer container)
            where TContainer : struct, IContainerValue<TContainer>
        {
            Debug.Assert(root.HoldsIds && container.HoldsIds, "only a container of classes is interned in a child's layers");

            var hash = container.ComputeHash();
            if (TryGetId(root, container, hash, out var id))
                return id;

            // The context freezes on its domain thread while none of its rule tasks runs, so the test holds through the
            // insert.
            if (frozen)
                ContainerEnv<TContainer>.ThrowInternedInFrozen();

            return GetOrCreateLayer(root).Own.GetOrInsertLocal(container, hash, idBlocks);
        }

    }

}

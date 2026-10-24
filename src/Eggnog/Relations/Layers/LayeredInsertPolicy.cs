using System;

namespace Eggnog.Relations
{

    /// <summary>
    /// A child context's way of putting a rebuilt container back into its layer of a type
    /// (<see cref="IContainerInsertPolicy{TContainer}"/>), over its view: the rebuilt container merges with an equal
    /// one wherever the child sees it, and lands in the layer's own environment only under an id the layer may own,
    /// claiming the id first when it may not (<see cref="ContainerLayers.Insert{TContainer}"/>). A full rebuild renames
    /// an entry in place only to an id the layer owns already; an entry whose new id it does not own is taken out and
    /// put back through <see cref="Insert"/>, which claims it.
    /// </summary>
    /// <typeparam name="TContainer">The container type.</typeparam>
    internal readonly struct LayeredInsertPolicy<TContainer> : IContainerInsertPolicy<TContainer>
        where TContainer : struct, IContainerValue<TContainer>
    {

        readonly ContainerLayers layers;
        readonly ContainerLayer<TContainer> layer;
        readonly ContainerEnv<TContainer> root;

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="layers">The child's container layers.</param>
        /// <param name="layer">Its layer of the type, whose own environment is rebuilt.</param>
        /// <param name="root">The root's environment of the type.</param>
        public LayeredInsertPolicy(ContainerLayers layers, ContainerLayer<TContainer> layer, ContainerEnv<TContainer> root)
        {
            ArgumentNullException.ThrowIfNull(layers);
            ArgumentNullException.ThrowIfNull(layer);
            ArgumentNullException.ThrowIfNull(root);
            this.layers = layers;
            this.layer = layer;
            this.root = root;
        }

        /// <inheritdoc />
        public bool RenamesInPlace(uint id) => layers.Owns(layer, id);

        /// <inheritdoc />
        public uint Insert(ContainerEnv<TContainer> env, in TContainer container, ulong hash, uint id, ExecutionState state) =>
            layers.Insert(root, layer, container, hash, id, state);

    }

}

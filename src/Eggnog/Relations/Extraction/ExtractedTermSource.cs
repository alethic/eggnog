namespace Eggnog.Relations
{

    /// <summary>
    /// What the terms of one extraction were extracted from, shared by every <see cref="ExtractedTerm"/> node it builds
    /// (<see cref="ExtractedTermBuilder"/>): the e-graph, which prints base values, and the containers of the context
    /// extracted from (<see cref="ContainerView"/>), which rebuild container terms. One per extraction, so that a node
    /// holds one reference for both.
    /// </summary>
    /// <remarks>
    /// Immutable and thread-safe.
    /// </remarks>
    internal sealed class ExtractedTermSource
    {

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="egraph">The e-graph the terms are extracted from.</param>
        /// <param name="containers">The containers of the context the terms are extracted from.</param>
        public ExtractedTermSource(EGraph egraph, ContainerView containers)
        {
            EGraph = egraph;
            Containers = containers;
        }

        /// <summary>
        /// Gets the e-graph the terms were extracted from, which prints their base values.
        /// </summary>
        public EGraph EGraph { get; }

        /// <summary>
        /// Gets the containers of the context the terms were extracted from, through which their containers' terms are
        /// rebuilt.
        /// </summary>
        public ContainerView Containers { get; }

    }

}

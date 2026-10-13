namespace Eggnog
{

    /// <summary>
    /// A label a scope carries: a scoped atom (<c>handle.In(tag)</c>, <see cref="Fact.In"/>) names the nearest
    /// ancestor-or-self of the running scope that carries it. Made by <see cref="EGraph.DefineScopeTag"/> and owned by
    /// that graph; compared by reference. The root carries <see cref="EGraph.RootTag"/>.
    /// </summary>
    /// <remarks>
    /// Immutable. Every member that takes a tag refuses another graph's with <see cref="System.ArgumentException"/>, so
    /// one graph's tags and frozen scopes never affect another's theory.
    /// </remarks>
    public sealed class ScopeTag
    {

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="graph">The graph that owns the tag.</param>
        /// <param name="name">The tag's name, unique in its graph.</param>
        /// <param name="serial">The tag's position among the graph's tags, from 0 (the root's).</param>
        internal ScopeTag(EGraph graph, string name, int serial)
        {
            Graph = graph;
            Name = name;
            Serial = serial;
        }

        /// <summary>
        /// Gets the graph that owns the tag.
        /// </summary>
        public EGraph Graph { get; }

        /// <summary>
        /// Gets the tag's name, unique in its graph: <c>root</c> for the root's.
        /// </summary>
        public string Name { get; }

        /// <summary>
        /// Gets the tag's name.
        /// </summary>
        /// <returns>The name.</returns>
        public override string ToString() => Name;

        /// <summary>
        /// Gets the tag's position among its graph's tags, from 0 (the root's): the index of its per-tag state in the
        /// graph's mirror registry.
        /// </summary>
        internal int Serial { get; }

    }

}

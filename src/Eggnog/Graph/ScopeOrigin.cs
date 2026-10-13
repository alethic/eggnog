namespace Eggnog
{

    /// <summary>
    /// Where a class value came from: a scope, or one host write batch of a scope. Every <see cref="ClassId"/> the
    /// library returns carries the origin of the scope (or batch) that served the call, and every entry point that takes
    /// a class value checks it (<see cref="ClassChecks.Require"/>).
    /// </summary>
    /// <remarks>
    /// A scope's origin lives as long as the scope and is marked <see cref="Disposed"/> when the scope is disposed, which
    /// kills every value it returned. A batch's origin is valid within its batch, and afterwards only once
    /// <see cref="Committed"/>. A scope's origin's parent is its parent scope's origin. The flags only ever go from false
    /// to true.
    /// </remarks>
    internal sealed class ScopeOrigin
    {

        volatile bool committed;
        volatile bool disposed;

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="graph">The graph the origin belongs to.</param>
        /// <param name="parent">A scope's: its parent scope's origin (<c>null</c> at the root). A batch's: its scope's
        /// origin.</param>
        /// <param name="path">The path of the scope, for messages.</param>
        /// <param name="isBatch">Whether this is a host batch's origin rather than a scope's.</param>
        public ScopeOrigin(EGraph graph, ScopeOrigin? parent, string path, bool isBatch)
        {
            Graph = graph;
            Parent = parent;
            Path = path;
            IsBatch = isBatch;
        }

        /// <summary>
        /// Gets the graph the origin belongs to.
        /// </summary>
        public EGraph Graph { get; }

        /// <summary>
        /// Gets, for a scope, its parent scope's origin (<c>null</c> at the root); for a batch, its scope's origin.
        /// </summary>
        public ScopeOrigin? Parent { get; }

        /// <summary>
        /// Gets the path of the scope (for a batch, of its scope), for messages.
        /// </summary>
        public string Path { get; }

        /// <summary>
        /// Gets whether this is a host batch's origin.
        /// </summary>
        public bool IsBatch { get; }

        /// <summary>
        /// Gets or sets, for a batch, whether it committed: its values are valid from then on.
        /// </summary>
        public bool Committed
        {
            get => committed;
            set => committed = value;
        }

        /// <summary>
        /// Gets or sets, for a scope, whether it was disposed: its values are dead from then on.
        /// </summary>
        public bool Disposed
        {
            get => disposed;
            set => disposed = value;
        }

        /// <inheritdoc />
        public override string ToString() => IsBatch ? Path + " (batch)" : Path;

    }

}

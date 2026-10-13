using System.Runtime.CompilerServices;

namespace Eggnog.Relations
{

    /// <summary>
    /// A context's layered union-find as an <see cref="IIdFinder"/>: <see cref="LayeredUnionFind.Find"/>, the root's
    /// find followed by one probe per non-empty overlay level. <see cref="OverlayCanonicalizer"/> runs
    /// <see cref="CanonicalizeLoops"/> with it.
    /// </summary>
    internal readonly struct LayeredIdFinder : IIdFinder
    {

        readonly LayeredUnionFind finder;

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="finder">The layered union-find.</param>
        public LayeredIdFinder(LayeredUnionFind finder)
        {
            this.finder = finder;
        }

        /// <inheritdoc />
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public uint Find(uint id) => finder.Find(id);

    }

}

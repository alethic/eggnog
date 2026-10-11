using System.Runtime.CompilerServices;

namespace Eggnog.Relations
{

    /// <summary>
    /// The root union-find as an <see cref="IIdFinder"/>: <see cref="DisplacedUnionFind.FindNaive"/>, the find egglog's
    /// <c>Canonicalizer</c> calls (core-relations/src/uf/mod.rs:62-196). <see cref="Canonicalizer"/> runs
    /// <see cref="CanonicalizeLoops"/> with it.
    /// </summary>
    internal readonly struct RootIdFinder : IIdFinder
    {

        readonly DisplacedUnionFind uf;

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="uf">The union-find.</param>
        public RootIdFinder(DisplacedUnionFind uf)
        {
            this.uf = uf;
        }

        /// <inheritdoc />
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public uint Find(uint id) => uf.FindNaive(id);

    }

}

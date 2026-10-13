using System;
using System.Runtime.CompilerServices;

namespace Eggnog.Relations
{

    /// <summary>
    /// A read-only view of a <see cref="UnionFind"/>: the C# form of the <c>&amp;UnionFind</c> that egglog's
    /// <c>DisplacedTable::underlying_uf</c> hands out (<c>core-relations/src/uf/mod.rs:463-465</c>), through which only
    /// <c>find_naive</c> can be called.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The view exposes no member that writes, so a holder can canonicalize values but cannot link two identifiers,
    /// reserve one or reset the union-find behind the back of the owner (for <see cref="DisplacedUnionFind"/>, that
    /// would displace an identifier without logging a row). It is live, not a snapshot: it sees every later change
    /// the owner makes.
    /// </para>
    /// <para>
    /// Threading: the same as <see cref="UnionFind.FindNaive"/>; safe from many threads while the owner does not
    /// mutate. Cost: the view is one reference; every member forwards to the union-find at no extra cost once
    /// inlined. <c>default</c> wraps no union-find and must not be used.
    /// </para>
    /// </remarks>
    internal readonly struct ReadOnlyUnionFind
    {

        /// <summary>
        /// The viewed union-find.
        /// </summary>
        readonly UnionFind uf;

        /// <summary>
        /// Initializes a view of <paramref name="uf"/>. Constant time.
        /// </summary>
        /// <param name="uf">The union-find to view.</param>
        /// <exception cref="ArgumentNullException"><paramref name="uf"/> is <c>null</c>.</exception>
        public ReadOnlyUnionFind(UnionFind uf)
        {
            ArgumentNullException.ThrowIfNull(uf);
            this.uf = uf;
        }

        /// <summary>
        /// Gets one more than the highest identifier reserved (<see cref="UnionFind.IdCount"/>). Constant time.
        /// </summary>
        public int IdCount => uf.IdCount;

        /// <summary>
        /// Gets the parent of every reserved identifier (<see cref="UnionFind.Parents"/>), valid until the owner's
        /// next mutating call. Constant time.
        /// </summary>
        public ReadOnlySpan<uint> Parents => uf.Parents;

        /// <summary>
        /// Finds the canonical identifier of <paramref name="id"/> without writing (<see cref="UnionFind.FindNaive"/>).
        /// An identifier never reserved is its own canonical identifier. O(path length).
        /// </summary>
        /// <param name="id">The identifier.</param>
        /// <returns>The canonical identifier of its class, the minimum member.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public uint FindNaive(uint id) => uf.FindNaive(id);

    }

}

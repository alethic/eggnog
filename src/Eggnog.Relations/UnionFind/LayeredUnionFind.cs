using System;
using System.Runtime.CompilerServices;

namespace Eggnog.Relations
{

    /// <summary>
    /// The find of a context: the root's union-find, then an optional flattened prefix for a frozen chain of ancestors,
    /// then one <see cref="UnionFindOverlay"/> per remaining level (colored-contexts spec §5.4):
    /// <c>find_C = overlay_C ∘ find_P</c>, unrolled.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Immutable: <see cref="Extend"/> returns a new chain. The levels are referenced, not copied, so a chain sees its
    /// contexts' overlays as they are now; that is correct whenever every level is current with its parent, which reads
    /// require (a context's reads need it clean; its writes and runs catch up first; a frozen context's parent is
    /// frozen). Keys that a parent displaced break a lazy "alternating chase", which is why contexts re-key eagerly
    /// (§5.4, §5.6).
    /// </para>
    /// <para>
    /// Cost of <see cref="Find"/>: the root's <see cref="DisplacedUnionFind.FindNaive"/> (one hop once the root is frozen
    /// and compressed), at most one dense load or probe for the prefix, and one probe per non-empty level below it. Never
    /// writes, so any number of threads may find concurrently while no level changes. Nothing is allocated per find.
    /// </para>
    /// </remarks>
    internal sealed class LayeredUnionFind
    {

        readonly DisplacedUnionFind root;
        readonly FlattenedUnionFind? prefix;
        readonly UnionFindOverlay[] levels;

        /// <summary>
        /// Initializes the find of the root context: the root's union-find alone.
        /// </summary>
        /// <param name="root">The root's union-find.</param>
        public LayeredUnionFind(DisplacedUnionFind root)
            : this(root, null, Array.Empty<UnionFindOverlay>())
        {

        }

        LayeredUnionFind(DisplacedUnionFind root, FlattenedUnionFind? prefix, UnionFindOverlay[] levels)
        {
            ArgumentNullException.ThrowIfNull(root);
            this.root = root;
            this.prefix = prefix;
            this.levels = levels;
        }

        /// <summary>
        /// Gets the root's union-find.
        /// </summary>
        public DisplacedUnionFind Root => root;

        /// <summary>
        /// Gets the flattened prefix applied after the root's find, or <c>null</c>.
        /// </summary>
        public FlattenedUnionFind? Prefix => prefix;

        /// <summary>
        /// Gets the overlay levels applied after the prefix, outermost first.
        /// </summary>
        public ReadOnlySpan<UnionFindOverlay> Levels => levels;

        /// <summary>
        /// Gets the find of a child whose own level is <paramref name="own"/>: this chain plus one level.
        /// </summary>
        /// <param name="own">The child's overlay.</param>
        /// <returns>The new chain; this one is unchanged.</returns>
        public LayeredUnionFind Extend(UnionFindOverlay own)
        {
            ArgumentNullException.ThrowIfNull(own);
            var extended = new UnionFindOverlay[levels.Length + 1];
            levels.CopyTo(extended, 0);
            extended[levels.Length] = own;
            return new LayeredUnionFind(root, prefix, extended);
        }

        /// <summary>
        /// Gets the find that a frozen context gives its children (§5.4, §8.1): the root's union-find followed by the
        /// frozen chain's flattened map, one probe however deep the chain is.
        /// </summary>
        /// <param name="root">The root's union-find.</param>
        /// <param name="prefix">The frozen chain flattened, or <c>null</c> when it maps every id to itself.</param>
        /// <returns>The chain.</returns>
        public static LayeredUnionFind Flattened(DisplacedUnionFind root, FlattenedUnionFind? prefix)
        {
            return new LayeredUnionFind(root, prefix, Array.Empty<UnionFindOverlay>());
        }

        /// <summary>
        /// Gets the canonical id of <paramref name="id"/> in the chain's context. Never writes.
        /// </summary>
        /// <param name="id">The id.</param>
        /// <returns>The canonical id: the minimum of its class in the context (I7).</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public uint Find(uint id)
        {
            var y = root.FindNaive(id);
            if (prefix is { } f)
                y = f.Map(y);

            var l = levels;
            for (var i = 0; i < l.Length; i++)
            {
                var o = l[i];
                if (o.IsEmpty == false)
                    y = o.Map(y);
            }

            return y;
        }

        /// <summary>
        /// Maps an id canonical in the root through the prefix and the levels: the part of <see cref="Find"/> above the
        /// root, which <see cref="FlattenedUnionFind.Build"/> tabulates.
        /// </summary>
        /// <param name="rootCanonical">An id canonical in the root.</param>
        /// <returns>Its canonical id in the chain's context.</returns>
        public uint MapAboveRoot(uint rootCanonical)
        {
            var y = rootCanonical;
            if (prefix is { } f)
                y = f.Map(y);

            foreach (var o in levels)
                if (o.IsEmpty == false)
                    y = o.Map(y);

            return y;
        }

    }

}

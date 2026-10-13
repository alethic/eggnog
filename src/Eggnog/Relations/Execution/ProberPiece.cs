using System;
using System.Runtime.CompilerServices;

namespace Eggnog.Relations
{

    /// <summary>
    /// The index of one layer of a <see cref="Prober"/>: egglog's <c>Prober</c> index part
    /// (core-relations/src/free_join/execute.rs:155-345) over the atom's rows of layer <see cref="Layer"/>. A root
    /// prober has exactly one piece, built with egglog's heuristics; a child's has one per layer of the atom's rows.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="Kind"/> says which index is read (see <see cref="ProberKind"/>); the other index fields are
    /// <c>null</c>. A cached index lists rows of the whole layer, so when the atom's rows of the layer are only part of it
    /// the result of every probe is clamped to <see cref="Outer"/> (egglog's <c>intersect_outer</c>), without allocating.
    /// </para>
    /// <para>
    /// Visibility replaces egglog's <c>PotentiallyStale</c> tag: <see cref="MayHoldInvisible"/> is set when the index can
    /// list rows the view does not see (a cached index, refreshed incrementally, keeps rows deleted after they were
    /// indexed, and never knows what a view hides: hides are not index content) and the layer has such rows
    /// (<see cref="LayerView.HasInvisible"/>); the stage then refines every probe result for visibility before using it.
    /// The other kinds are built from visibility-aware scans of a fixed snapshot and list only visible rows.
    /// </para>
    /// <para>
    /// A plain struct kept in per-position scratch, so building one allocates nothing beyond the index itself. Its fields
    /// are plain fields so compiled kernels can read them.
    /// </para>
    /// </remarks>
    internal struct ProberPiece
    {

        /// <summary>
        /// The index read.
        /// </summary>
        public ProberKind Kind;

        /// <summary>
        /// The layer of the atom's view the piece indexes.
        /// </summary>
        public byte Layer;

        /// <summary>
        /// Whether probe results are clamped to <see cref="Outer"/>.
        /// </summary>
        public bool HasOuter;

        /// <summary>
        /// Whether probe results may list rows the view does not see and must be refined for visibility.
        /// </summary>
        public bool MayHoldInvisible;

        /// <summary>
        /// The atom's rows of the layer, a dense range, when <see cref="HasOuter"/>.
        /// </summary>
        public OffsetRange Outer;

        /// <summary>
        /// The column index of a <see cref="ProberKind.CachedColumn"/> or <see cref="ProberKind.DynamicColumn"/>.
        /// </summary>
        public ColumnIndex? Column;

        /// <summary>
        /// The tuple index of a <see cref="ProberKind.CachedTuple"/> or <see cref="ProberKind.DynamicTuple"/>.
        /// </summary>
        public TupleIndex? Tuple;

        /// <summary>
        /// The index of a <see cref="ProberKind.Sparse"/>.
        /// </summary>
        public SparseColumnIndex? Sparse;

        /// <summary>
        /// The atom's view of the layer: its table, its catalogs and the rows the view hides.
        /// </summary>
        public LayerView? View;

        /// <summary>
        /// Gets the number of distinct keys, which picks the side an intersection iterates. egglog's <c>len</c>.
        /// </summary>
        /// <remarks>
        /// egglog returns the key count of the whole cached index even when the atom's rows are a sub-range of the
        /// table (core-relations/src/free_join/execute.rs:336-344), which can make a large range look smaller than a
        /// small dynamic index. Here a clamped cached index counts at most one key per row of the range. The count
        /// affects performance only.
        /// </remarks>
        public readonly int Count
        {
            get
            {
                switch (Kind)
                {
                    case ProberKind.Sparse:
                        return Sparse!.Count;
                    case ProberKind.CachedColumn:
                    case ProberKind.DynamicColumn:
                        return HasOuter ? Math.Min(Column!.Count, Outer.Size) : Column!.Count;
                    case ProberKind.CachedTuple:
                    case ProberKind.DynamicTuple:
                        return HasOuter ? Math.Min(Tuple!.Count, Outer.Size) : Tuple!.Count;
                    default:
                        return 0;
                }
            }
        }

        /// <summary>
        /// Looks up the rows of the layer holding <paramref name="key"/> in a one-column piece. egglog's
        /// <c>get_subset</c>.
        /// </summary>
        /// <param name="key">The value.</param>
        /// <param name="rows">The rows, nonempty and sorted; borrowed from the index.</param>
        /// <returns><c>true</c> if any of the atom's rows of the layer holds the value.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public readonly bool TryGetSubset(uint key, out SubsetRef rows)
        {
            if (Kind == ProberKind.Sparse)
                return Sparse!.TryGetSubset(key, out rows);

            if (Column!.TryGetSubset(key, out var all) == false)
            {
                rows = default;
                return false;
            }

            if (HasOuter)
                return all.TryIntersectDense(Outer, out rows);

            rows = all;
            return true;
        }

        /// <summary>
        /// Looks up the rows of the layer holding the key at <paramref name="key"/>[0..arity) in a multi-column piece.
        /// egglog's <c>get_subset</c>.
        /// </summary>
        /// <param name="key">The array holding the key, as many cells as the piece has columns.</param>
        /// <param name="rows">The rows, nonempty and sorted; borrowed from the index.</param>
        /// <returns><c>true</c> if any of the atom's rows of the layer holds the key.</returns>
        public readonly bool TryGetSubset(uint[] key, out SubsetRef rows)
        {
            if (Tuple!.TryGetSubset(key, 0, out var all) == false)
            {
                rows = default;
                return false;
            }

            if (HasOuter)
                return all.TryIntersectDense(Outer, out rows);

            rows = all;
            return true;
        }

    }

}

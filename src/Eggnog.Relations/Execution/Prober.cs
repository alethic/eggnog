using System;
using System.Runtime.CompilerServices;

namespace Eggnog.Relations
{

    /// <summary>
    /// An index over some columns of an atom's current rows, together with the atom's trie node, which the stage holds
    /// while it runs: egglog's <c>Prober</c> (core-relations/src/free_join/execute.rs:155-345).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="Kind"/> says which index is read (see <see cref="ProberKind"/>); the other index fields are
    /// <c>null</c>. A cached index lists rows of the whole table, so when the atom's rows are only part of it the
    /// result of every probe is clamped to <see cref="Outer"/> (egglog's <c>intersect_outer</c>), without allocating.
    /// </para>
    /// <para>
    /// Visibility replaces egglog's <c>PotentiallyStale</c> tag: <see cref="MayHoldInvisible"/> is set when the index can
    /// list rows the table no longer shows (a cached index, refreshed incrementally, keeps rows deleted after they were
    /// indexed) and the table has such rows; the stage then refines every probe result for visibility before using it
    /// (<c>refine_live</c>). The other kinds are built from visibility-aware scans of a fixed snapshot and list only
    /// visible rows.
    /// </para>
    /// <para>
    /// A plain struct kept in per-position scratch arrays, so building one allocates nothing beyond the index itself.
    /// </para>
    /// </remarks>
    internal struct Prober
    {

        /// <summary>
        /// The index read.
        /// </summary>
        public ProberKind Kind;

        /// <summary>
        /// The atom's trie node, moved out of the bindings for the stage's duration.
        /// </summary>
        public TrieNode Node;

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
        /// Whether probe results are clamped to <see cref="Outer"/>.
        /// </summary>
        public bool HasOuter;

        /// <summary>
        /// The atom's rows, a dense range, when <see cref="HasOuter"/>.
        /// </summary>
        public OffsetRange Outer;

        /// <summary>
        /// Whether probe results may list rows the table no longer shows and must be refined for visibility.
        /// </summary>
        public bool MayHoldInvisible;

        /// <summary>
        /// Gets the number of distinct keys, which picks the side an intersection iterates. egglog's <c>len</c>.
        /// </summary>
        /// <remarks>
        /// Fix (map §8.1 row 18, core-relations/src/free_join/execute.rs:336-344): egglog returns the key count of the
        /// whole cached index even when the atom's rows are a sub-range of the table, which can make a large range look
        /// smaller than a small dynamic index. Here a clamped cached index counts at most one key per row of the range.
        /// Performance only.
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
        /// Looks up the rows holding <paramref name="key"/> in a one-column prober. egglog's <c>get_subset</c>.
        /// </summary>
        /// <param name="key">The value.</param>
        /// <param name="rows">The rows, nonempty and sorted; borrowed from the index.</param>
        /// <returns><c>true</c> if any of the atom's rows holds the value.</returns>
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
        /// Looks up the rows holding the key at <paramref name="key"/>[0..arity) in a multi-column prober. egglog's
        /// <c>get_subset</c>.
        /// </summary>
        /// <param name="key">The array holding the key, as many cells as the prober has columns.</param>
        /// <param name="rows">The rows, nonempty and sorted; borrowed from the index.</param>
        /// <returns><c>true</c> if any of the atom's rows holds the key.</returns>
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

using System.Runtime.CompilerServices;

namespace Eggnog.Relations
{

    /// <summary>
    /// Walks the entries of a one-column <see cref="Prober"/>: egglog's <c>Prober::for_each</c>
    /// (core-relations/src/free_join/execute.rs:284-334) as an external cursor, so the stage's candidate loop is a
    /// plain loop that can drain and recurse between entries.
    /// </summary>
    /// <remarks>
    /// A clamped cached index skips the keys none of whose rows fall in the atom's range. <c>default</c> is
    /// positioned before the first entry; the prober must not change while the cursor is used.
    /// </remarks>
    internal struct ProberCursor
    {

        ColumnIndexCursor inner;
        int entry;
        uint key;
        SubsetRef rows;

        /// <summary>
        /// Gets the current value.
        /// </summary>
        public readonly uint Key => key;

        /// <summary>
        /// Gets the atom's rows holding the current value: nonempty, sorted, borrowed from the index.
        /// </summary>
        public readonly SubsetRef Rows => rows;

        /// <summary>
        /// Advances to the next entry of <paramref name="prober"/>.
        /// </summary>
        /// <param name="prober">The prober; one-column, the same on every call.</param>
        /// <returns><c>false</c> once every entry has been visited.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool MoveNext(in Prober prober)
        {
            if (prober.Kind == ProberKind.Sparse)
            {
                var sparse = prober.Sparse!;
                if (entry >= sparse.Count)
                    return false;

                key = sparse.KeyAt(entry);
                rows = sparse.RowsAt(entry);
                entry++;
                return true;
            }

            return MoveNextColumn(prober);
        }

        bool MoveNextColumn(in Prober prober)
        {
            var index = prober.Column!;
            while (inner.MoveNext(index))
            {
                if (prober.HasOuter == false)
                {
                    key = inner.Key;
                    rows = inner.Rows;
                    return true;
                }

                if (inner.Rows.TryIntersectDense(prober.Outer, out rows))
                {
                    key = inner.Key;
                    return true;
                }
            }

            return false;
        }

    }

}

namespace Eggnog.Relations
{

    /// <summary>
    /// The index a <see cref="Prober"/> reads: egglog's <c>DynamicIndex</c> variants
    /// (core-relations/src/free_join/execute.rs:155-171), chosen by <c>get_index</c> from the size and shape of the
    /// atom's current rows.
    /// </summary>
    internal enum ProberKind : byte
    {

        /// <summary>
        /// No index: the prober is unused.
        /// </summary>
        None,

        /// <summary>
        /// At most eight rows and one column: a <see cref="SparseColumnIndex"/> built on the spot.
        /// </summary>
        Sparse,

        /// <summary>
        /// One column of a dense row set covering more than half the table: the database's cached
        /// <see cref="ColumnIndex"/>, clamped to the row set's range.
        /// </summary>
        CachedColumn,

        /// <summary>
        /// Several columns of a dense row set covering more than half the table: the database's cached
        /// <see cref="TupleIndex"/>, clamped to the row set's range.
        /// </summary>
        CachedTuple,

        /// <summary>
        /// One column of any other row set: a <see cref="ColumnIndex"/> cached on the atom's trie node.
        /// </summary>
        DynamicColumn,

        /// <summary>
        /// Several columns of any other row set: a <see cref="TupleIndex"/> built for this stage and disposed after it.
        /// </summary>
        DynamicTuple,

    }

}

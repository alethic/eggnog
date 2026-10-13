using System;

namespace Eggnog.Relations
{

    /// <summary>
    /// Resolves a conflict between the row a <see cref="SortedWritesTable"/> holds for a key and an incoming row with
    /// the same key: egglog's <c>MergeFn</c> (core-relations/src/table/mod.rs:126-134).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="Merge"/> returns <c>true</c> when the row must change; it has then written the whole new row to
    /// <c>output</c>, which the table stores, in the parallel path too, where egglog's parallel insert discards the
    /// merge output (core-relations/src/table/mod.rs:902-907). It returns <c>false</c> to keep the current row, in
    /// which case nothing is written and no delta is produced: re-deriving a fact that is already present must return
    /// <c>false</c> or saturation never ends.
    /// </para>
    /// <para>
    /// The output must keep the key columns (the table reuses the key's hash entry) and, in a table sorted by a
    /// timestamp column, the incoming row's timestamp (the run index is updated from the incoming row). Debug builds
    /// assert both.
    /// </para>
    /// <para>
    /// Merges may stage writes through the <see cref="ExecutionState"/> (a union into the union-find, say). They run
    /// from several threads at once during a parallel merge, each with its own clone of the state, so they must not
    /// keep mutable state of their own. Within one batch, rows with the same key fold left to right
    /// (<c>r := current; for each incoming q, if Merge(r, q, out) then r := out</c>), but the parallel path first folds
    /// the batch and then merges the result into the table, which agrees with the serial order only for associative
    /// merges; and the order of batches from different threads is not fixed. <see cref="IsLattice"/> declares whether
    /// the merge is insensitive to all of that.
    /// </para>
    /// <para>
    /// A table calls <see cref="Merge"/> once per conflicting row, through one virtual call, as egglog calls its boxed
    /// closure; the sealed built-ins let the JIT devirtualize monomorphic call sites.
    /// </para>
    /// </remarks>
    internal abstract class MergeFunction
    {

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="isLattice">Whether the merge is associative, commutative and idempotent.</param>
        protected MergeFunction(bool isLattice)
        {
            IsLattice = isLattice;
        }

        /// <summary>
        /// Gets whether the merge is a lattice merge: associative, commutative and idempotent, so that merge order,
        /// batching and repetition do not change the result. Only lattice merges may be written from a context that has
        /// ancestors.
        /// </summary>
        public bool IsLattice { get; }

        /// <summary>
        /// Merges <paramref name="incoming"/> into <paramref name="current"/>.
        /// </summary>
        /// <param name="state">The execution state, for staging writes into other tables.</param>
        /// <param name="current">The row the table holds for the key.</param>
        /// <param name="incoming">The incoming row with the same key.</param>
        /// <param name="output">Receives the new row when the result is <c>true</c>; as long as the rows.</param>
        /// <returns><c>true</c> if the row changes to <paramref name="output"/>; <c>false</c> to keep the current row.</returns>
        public abstract bool Merge(ExecutionState state, ReadOnlySpan<uint> current, ReadOnlySpan<uint> incoming, Span<uint> output);

    }

}

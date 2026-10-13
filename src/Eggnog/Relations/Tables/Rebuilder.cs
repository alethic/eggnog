using System;

namespace Eggnog.Relations
{

    /// <summary>
    /// A bulk, value-level rewrite that one table (the union-find) provides for the columns of another: egglog's
    /// <c>Rebuilder</c> trait (core-relations/src/table_spec.rs:103-135). A table rebuilds by staging, for every row the
    /// rewrite changes, a removal of the old key and an insertion of the rewritten row; merging afterwards resolves key
    /// collisions with the table's merge function, which is what gives congruence.
    /// </summary>
    /// <remarks>
    /// The only implementation is the union-find's <see cref="Canonicalizer"/>. Callers make one virtual call per chunk
    /// of rows, never per row: the per-row loops live inside the sealed implementation.
    /// </remarks>
    internal abstract class Rebuilder
    {

        /// <summary>
        /// Gets the column of the providing table whose recently appended rows name exactly the values that changed,
        /// or <c>null</c>. When set, a table can rebuild incrementally from the provider's rows appended since its last
        /// rebuild. egglog's <c>hint_col</c>.
        /// </summary>
        public abstract ColumnId? HintColumn { get; }

        /// <summary>
        /// Rewrites one value. egglog's <c>rebuild_val</c>.
        /// </summary>
        /// <param name="value">The value.</param>
        /// <returns>The rewritten value.</returns>
        public abstract uint RebuildValue(uint value);

        /// <summary>
        /// Rewrites the live rows <c>[start, end)</c> of <paramref name="buffer"/>, appending to
        /// <paramref name="output"/>, tagged with their row ids, exactly the rows the rewrite changes. egglog's
        /// <c>rebuild_buf</c>.
        /// </summary>
        /// <param name="buffer">A table's row buffer.</param>
        /// <param name="start">The first row.</param>
        /// <param name="end">One past the last row; at most the buffer's row count.</param>
        /// <param name="output">Receives the rewritten rows.</param>
        /// <param name="state">The execution state.</param>
        public abstract void RebuildBuffer(RowBuffer buffer, int start, int end, TaggedRowBuffer output, ExecutionState state);

        /// <summary>
        /// Rewrites the live rows of <paramref name="subset"/> of <paramref name="table"/>, appending every one to
        /// <paramref name="output"/> tagged with its row id and marking stale the copies the rewrite did not change.
        /// egglog's <c>rebuild_subset</c>.
        /// </summary>
        /// <param name="table">The table being rebuilt.</param>
        /// <param name="subset">The rows to rewrite.</param>
        /// <param name="output">Receives the rows.</param>
        /// <param name="state">The execution state.</param>
        public abstract void RebuildSubset(Table table, SubsetRef subset, TaggedRowBuffer output, ExecutionState state);

        /// <summary>
        /// Rewrites values in place. egglog's <c>rebuild_slice</c>, used for container contents.
        /// </summary>
        /// <param name="values">The values.</param>
        /// <returns><c>true</c> if any value changed.</returns>
        public abstract bool RebuildSlice(Span<uint> values);

    }

}

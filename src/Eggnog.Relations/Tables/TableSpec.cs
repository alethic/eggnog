using System;

namespace Eggnog.Relations
{

    /// <summary>
    /// The schema of a table: egglog's <c>TableSpec</c> (core-relations/src/table_spec.rs:53-81). Immutable: callers may
    /// assume it never changes for a table.
    /// </summary>
    /// <remarks>
    /// The first <see cref="KeyCount"/> columns are the key, which functionally determines the remaining
    /// <see cref="ValueCount"/> columns. Uncacheable columns change without a version bump (the union-find's canonical
    /// column), so nothing may build an index on them.
    /// </remarks>
    internal sealed class TableSpec
    {

        readonly bool[] uncacheable;

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="keyCount">The number of key columns; may be zero.</param>
        /// <param name="valueCount">The number of value columns.</param>
        /// <param name="allowsDelete">Whether <see cref="MutationBuffer.StageRemove(ReadOnlySpan{uint})"/> is allowed.</param>
        /// <param name="uncacheableColumns">The columns that must never be indexed.</param>
        public TableSpec(int keyCount, int valueCount, bool allowsDelete, ReadOnlySpan<ColumnId> uncacheableColumns = default)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(keyCount);
            ArgumentOutOfRangeException.ThrowIfNegative(valueCount);

            KeyCount = keyCount;
            ValueCount = valueCount;
            AllowsDelete = allowsDelete;
            uncacheable = new bool[keyCount + valueCount];
            foreach (var column in uncacheableColumns)
            {
                if ((uint)column.Rep >= (uint)uncacheable.Length)
                    throw new ArgumentOutOfRangeException(nameof(uncacheableColumns), column, "The column is not part of the table.");

                uncacheable[column.Rep] = true;
            }
        }

        /// <summary>
        /// Gets the number of key columns. egglog's <c>n_keys</c>.
        /// </summary>
        public int KeyCount { get; }

        /// <summary>
        /// Gets the number of value (non-key) columns. egglog's <c>n_vals</c>.
        /// </summary>
        public int ValueCount { get; }

        /// <summary>
        /// Gets the total number of columns, <see cref="KeyCount"/> + <see cref="ValueCount"/>.
        /// </summary>
        public int Arity => KeyCount + ValueCount;

        /// <summary>
        /// Gets whether the table supports deletions; tables that do not throw on a staged removal.
        /// </summary>
        public bool AllowsDelete { get; }

        /// <summary>
        /// Gets whether <paramref name="column"/> must not be cached or indexed. O(1).
        /// </summary>
        /// <param name="column">The column.</param>
        /// <returns><c>true</c> if the column is uncacheable.</returns>
        public bool IsUncacheable(ColumnId column) => (uint)column.Rep < (uint)uncacheable.Length && uncacheable[column.Rep];

    }

}

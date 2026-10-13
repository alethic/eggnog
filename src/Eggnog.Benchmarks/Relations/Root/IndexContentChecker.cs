using System;

namespace Eggnog.Relations.Benchmarks
{

    /// <summary>
    /// An index visitor that checks what a <see cref="ColumnIndex"/> or <see cref="TupleIndex"/> lists: it counts the
    /// keys and the rows listed under them, reads every listed row back from the table to check that it is live and that
    /// its key cells equal the key it is listed under, and folds every (row, key) entry into an order-independent
    /// checksum. This is how <see cref="IndexRefreshBenchmarks"/> checks that a refresh indexed exactly the table's rows,
    /// each under its own key.
    /// </summary>
    /// <remarks>
    /// <para>
    /// With the key count, the row count and no misplaced row, the checksum equals the sum of
    /// <see cref="Entry(int, ReadOnlySpan{uint})"/> over the table's live rows and their keys exactly when the listed
    /// (row, key) entries are the table's entries, up to a 64-bit hash collision: an index that lists one row twice and
    /// another not at all, under the right keys and counts, changes the sum.
    /// </para>
    /// <para>
    /// For checks outside the measurement only: O(keys + listed rows × key columns), reading the table's rows.
    /// </para>
    /// </remarks>
    internal struct IndexContentChecker : IColumnIndexVisitor, ITupleIndexVisitor
    {

        readonly RowBuffer rows;
        readonly ColumnId[] keyColumns;

        /// <summary>
        /// Initializes a checker for an index of <paramref name="table"/> keyed by <paramref name="keyColumns"/>.
        /// </summary>
        /// <param name="table">The indexed table, at the version the index was refreshed to.</param>
        /// <param name="keyColumns">The index's key columns, in key order.</param>
        public IndexContentChecker(SortedWritesTable table, ColumnId[] keyColumns)
        {
            ArgumentNullException.ThrowIfNull(table);
            ArgumentNullException.ThrowIfNull(keyColumns);

            rows = table.Rows;
            this.keyColumns = keyColumns;
            Keys = 0;
            Rows = 0;
            Misplaced = 0;
            Checksum = 0;
        }

        /// <summary>
        /// Gets the number of keys visited.
        /// </summary>
        public long Keys { get; private set; }

        /// <summary>
        /// Gets the number of rows listed under the keys visited.
        /// </summary>
        public long Rows { get; private set; }

        /// <summary>
        /// Gets the number of listed rows that are out of the table's range, deleted, or whose key cells differ from the
        /// key they are listed under.
        /// </summary>
        public long Misplaced { get; private set; }

        /// <summary>
        /// Gets the sum of <see cref="Entry(int, ReadOnlySpan{uint})"/> over every listed (row, key) entry, wrapping.
        /// </summary>
        public ulong Checksum { get; private set; }

        /// <inheritdoc />
        public void Visit(uint key, SubsetRef rows)
        {
            ReadOnlySpan<uint> cells = [key];
            VisitKey(cells, rows);
        }

        /// <inheritdoc />
        public void Visit(ReadOnlySpan<uint> key, SubsetRef rows) => VisitKey(key, rows);

        /// <summary>
        /// Checks <paramref name="index"/> against <paramref name="table"/>. O(keys + listed rows).
        /// </summary>
        /// <param name="index">The index.</param>
        /// <param name="table">The indexed table.</param>
        /// <param name="keyColumns">The index's key column (one).</param>
        /// <returns>The checker after visiting every key.</returns>
        public static IndexContentChecker Check(ColumnIndex index, SortedWritesTable table, ColumnId[] keyColumns)
        {
            ArgumentNullException.ThrowIfNull(index);

            var checker = new IndexContentChecker(table, keyColumns);
            index.ForEach(ref checker);
            return checker;
        }

        /// <summary>
        /// Checks <paramref name="index"/> against <paramref name="table"/>. O(keys + listed rows × key columns).
        /// </summary>
        /// <param name="index">The index.</param>
        /// <param name="table">The indexed table.</param>
        /// <param name="keyColumns">The index's key columns, in key order.</param>
        /// <returns>The checker after visiting every key.</returns>
        public static IndexContentChecker Check(TupleIndex index, SortedWritesTable table, ColumnId[] keyColumns)
        {
            ArgumentNullException.ThrowIfNull(index);

            var checker = new IndexContentChecker(table, keyColumns);
            index.ForEach(ref checker);
            return checker;
        }

        /// <summary>
        /// Hashes one (row, key) entry: a SplitMix64 finalizer chained over the row id and the key cells.
        /// </summary>
        /// <param name="row">The row id.</param>
        /// <param name="key">The key cells.</param>
        /// <returns>The entry's hash.</returns>
        public static ulong Entry(int row, ReadOnlySpan<uint> key)
        {
            var hash = Mix((ulong)(uint)row);
            foreach (var cell in key)
                hash = Mix(hash ^ cell);

            return hash;
        }

        /// <summary>
        /// The SplitMix64 finalizer.
        /// </summary>
        /// <param name="value">The value to mix.</param>
        /// <returns>The mixed value.</returns>
        static ulong Mix(ulong value)
        {
            unchecked
            {
                value += 0x9E3779B97F4A7C15UL;
                value = (value ^ (value >> 30)) * 0xBF58476D1CE4E5B9UL;
                value = (value ^ (value >> 27)) * 0x94D049BB133111EBUL;
                return value ^ (value >> 31);
            }
        }

        /// <summary>
        /// Counts one key, and checks and sums every row listed under it.
        /// </summary>
        /// <param name="key">The key's cells.</param>
        /// <param name="listed">The rows listed under it.</param>
        void VisitKey(ReadOnlySpan<uint> key, SubsetRef listed)
        {
            Keys++;
            Rows += listed.Size;
            foreach (var row in listed)
            {
                if (IsListedCorrectly(row, key) == false)
                    Misplaced++;

                Checksum = unchecked(Checksum + Entry(row, key));
            }
        }

        /// <summary>
        /// Gets whether <paramref name="row"/> is a live row of the table whose key cells equal <paramref name="key"/>.
        /// </summary>
        /// <param name="row">The listed row id.</param>
        /// <param name="key">The key it is listed under.</param>
        /// <returns><c>true</c> if the row belongs under the key.</returns>
        readonly bool IsListedCorrectly(int row, ReadOnlySpan<uint> key)
        {
            if ((uint)row >= (uint)rows.Count || rows.IsStale(row) || key.Length != keyColumns.Length)
                return false;

            var cells = rows.GetRow(row);
            for (var c = 0; c < keyColumns.Length; c++)
                if (cells[keyColumns[c].Rep] != key[c])
                    return false;

            return true;
        }

    }

}

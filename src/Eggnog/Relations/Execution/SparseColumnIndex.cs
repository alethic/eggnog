using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace Eggnog.Relations
{

    /// <summary>
    /// A column index over at most <see cref="Capacity"/> rows, built by sorting: egglog's <c>SparseColumnIndex</c>
    /// (core-relations/src/free_join/execute.rs:47-153), which the join uses instead of a hash index when an atom's
    /// current rows are this few.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Layout: the distinct values in ascending order, the start of each value's run, and the row ids sorted by
    /// (value, row), in fixed arrays of <see cref="Capacity"/>. A probe is a binary search over at most eight keys; a run
    /// of one row comes back as a dense range so that copying it needs no buffer (egglog's <c>sparse_subset_ref</c>).
    /// </para>
    /// <para>
    /// Built from a visibility-aware column scan, so it lists live rows only. Reused: the executor keeps a stack of
    /// these per join task and rebuilds one per probe; subsets read from it are valid until its next
    /// <see cref="Build(Table, SubsetRef, ColumnId)"/>. Not thread-safe.
    /// </para>
    /// </remarks>
    internal sealed class SparseColumnIndex
    {

        /// <summary>
        /// The most rows the index holds: egglog's <c>SMALL_RESIDUAL</c>.
        /// </summary>
        public const int Capacity = 8;

        readonly uint[] keys = new uint[Capacity];
        readonly int[] starts = new int[Capacity + 1];
        readonly int[] rows = new int[Capacity];
        readonly ulong[] pairs = new ulong[Capacity];
        int keyCount;

        /// <summary>
        /// Gets the number of distinct values. egglog's <c>len</c>.
        /// </summary>
        public int Count => keyCount;

        /// <summary>
        /// Groups the live rows of <paramref name="subset"/> by <paramref name="column"/>. egglog's
        /// <c>SparseColumnIndex::new</c>. O(k log k) for k &lt;= <see cref="Capacity"/> rows.
        /// </summary>
        /// <param name="table">The table.</param>
        /// <param name="subset">The rows; at most <see cref="Capacity"/>.</param>
        /// <param name="column">The column.</param>
        public void Build(Table table, SubsetRef subset, ColumnId column) => Build(table, subset, column, HiddenRows.None);

        /// <summary>
        /// Groups the rows of <paramref name="subset"/> a view sees (live and not in <paramref name="hidden"/>) by
        /// <paramref name="column"/>: the visibility-aware <see cref="Build(Table, SubsetRef, ColumnId)"/> of a layered
        /// run's small probed pieces, whose probe results need no further visibility refinement. With
        /// <see cref="HiddenRows.None"/> it is the plain build.
        /// </summary>
        /// <remarks>
        /// One body for both builds, with the grouping written out after the scan as in egglog's <c>SparseColumnIndex::new</c>.
        /// The join builds one of these per small refined node, so this method is hot. Kept whole, its loops get it profiled
        /// by the tiered JIT; on net10.0 the JIT then inlines the plain column scan here, while on net8.0 the scan stays a
        /// call. A body reduced to calls is compiled without a profile and keeps the scan out of line on both.
        /// </remarks>
        /// <param name="table">The table: one layer of a view.</param>
        /// <param name="subset">The rows; at most <see cref="Capacity"/>.</param>
        /// <param name="column">The column.</param>
        /// <param name="hidden">The rows of the layer the view hides; sealed.</param>
        public void Build(Table table, SubsetRef subset, ColumnId column, HiddenRows hidden)
        {
            if (subset.Size > Capacity)
                throw new ArgumentException($"A sparse column index holds at most {Capacity} rows.", nameof(subset));

            var sink = new SparseColumnSink(pairs);
            if (hidden.IsNone)
                table.ForEachColumn(subset, column, ref sink);
            else
                table.ForEachColumn(subset, column, hidden, ref sink);

            var n = sink.Count;

            // Insertion sort: at most eight words, already sorted by row.
            for (var i = 1; i < n; i++)
            {
                var p = pairs[i];
                var j = i - 1;
                while (j >= 0 && pairs[j] > p)
                {
                    pairs[j + 1] = pairs[j];
                    j--;
                }

                pairs[j + 1] = p;
            }

            keyCount = 0;
            for (var i = 0; i < n; i++)
            {
                var key = (uint)(pairs[i] >> 32);
                if (keyCount == 0 || keys[keyCount - 1] != key)
                {
                    starts[keyCount] = i;
                    keys[keyCount++] = key;
                }

                rows[i] = (int)(uint)pairs[i];
            }

            starts[keyCount] = n;
        }

        /// <summary>
        /// Gets the value of entry <paramref name="entry"/>, in ascending value order.
        /// </summary>
        /// <param name="entry">The entry, in <c>[0, Count)</c>.</param>
        /// <returns>The value.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public uint KeyAt(int entry)
        {
            Debug.Assert((uint)entry < (uint)keyCount);
            return keys[entry];
        }

        /// <summary>
        /// Gets the rows of entry <paramref name="entry"/>.
        /// </summary>
        /// <param name="entry">The entry, in <c>[0, Count)</c>.</param>
        /// <returns>The rows, sorted; dense when there is one.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public SubsetRef RowsAt(int entry)
        {
            Debug.Assert((uint)entry < (uint)keyCount);
            return SubsetRef.FromSortedRun(rows, starts[entry], starts[entry + 1]);
        }

        /// <summary>
        /// Looks up the rows holding <paramref name="key"/>. egglog's <c>get_subset</c>.
        /// </summary>
        /// <param name="key">The value.</param>
        /// <param name="result">The rows, when present.</param>
        /// <returns><c>true</c> if some row holds the value.</returns>
        public bool TryGetSubset(uint key, out SubsetRef result)
        {
            var found = new ReadOnlySpan<uint>(keys, 0, keyCount).BinarySearch(key);
            if (found < 0)
            {
                result = default;
                return false;
            }

            result = SubsetRef.FromSortedRun(rows, starts[found], starts[found + 1]);
            return true;
        }

    }

}

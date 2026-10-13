using System;
using System.Runtime.CompilerServices;

namespace Eggnog.Relations
{

    /// <summary>
    /// An <see cref="IRowSink"/> that, for each scanned value, collects every row a <see cref="ColumnIndex"/> lists under
    /// it: how an incremental rebuild turns the union-find's newly displaced ids into the rows of a table that mention
    /// them (egglog's loop over <c>rebuild_index.get_subset</c>, core-relations/src/table/rebuild.rs:175-183).
    /// </summary>
    /// <remarks>
    /// Feed it one-cell rows (<see cref="Table.ForEachColumn{TSink}(SubsetRef, ColumnId, ref TSink)"/>). The rows
    /// accumulate in a pooled array in probe order, duplicates included; <see cref="SortUnique"/> turns them into a
    /// sorted set. A struct passed by reference: read <see cref="Rows"/> and <see cref="Count"/> after the scan, and
    /// <see cref="Release"/> the array when done.
    /// </remarks>
    internal struct ColumnIndexGatherSink : IRowSink
    {

        readonly ColumnIndex index;
        int[] rows;
        int count;

        /// <summary>
        /// Initializes a sink probing <paramref name="index"/>.
        /// </summary>
        /// <param name="index">The index.</param>
        public ColumnIndexGatherSink(ColumnIndex index)
        {
            this.index = index;
            rows = Array.Empty<int>();
            count = 0;
        }

        /// <summary>
        /// Gets the array holding the collected rows at positions <c>[0, Count)</c>.
        /// </summary>
        public readonly int[] Rows => rows;

        /// <summary>
        /// Gets the number of collected rows.
        /// </summary>
        public readonly int Count => count;

        /// <inheritdoc />
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void AddRow(int rowId, ReadOnlySpan<uint> row)
        {
            if (index.TryGetSubset(row[0], out var subset))
                Append(subset);
        }

        /// <summary>
        /// Collects every row the index lists under <paramref name="value"/>: one probe, as <see cref="AddRow"/> makes for
        /// a scanned one-cell row. A child context probes an ancestor layer's rebuild index with it: when it rebuilds,
        /// with each id its own unions displaced, and when it lifts, with one canonical id. O(1) expected plus the rows
        /// listed.
        /// </summary>
        /// <param name="value">The value.</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void AddValue(uint value)
        {
            if (index.TryGetSubset(value, out var subset))
                Append(subset);
        }

        /// <summary>
        /// Appends the rows <c>[start, end)</c> themselves, whatever they hold. O(end - start).
        /// </summary>
        /// <param name="start">The first row.</param>
        /// <param name="end">The row after the last.</param>
        public void AppendRange(int start, int end)
        {
            if (start < end)
                Append(SubsetRef.Dense(new OffsetRange(start, end)));
        }

        /// <summary>
        /// Sorts the collected rows and removes duplicates. O(n log n).
        /// </summary>
        /// <returns>The number of distinct rows, now at positions <c>[0, Count)</c> in ascending order.</returns>
        public int SortUnique()
        {
            var span = rows.AsSpan(0, count);
            span.Sort();
            var write = 0;
            for (var read = 0; read < span.Length; read++)
            {
                if (write == 0 || span[write - 1] != span[read])
                    span[write++] = span[read];
            }

            count = write;
            return write;
        }

        /// <summary>
        /// Returns the array to the pool.
        /// </summary>
        public void Release()
        {
            PooledArray.Release(rows);
            rows = Array.Empty<int>();
            count = 0;
        }

        /// <summary>
        /// Appends the rows of <paramref name="subset"/>.
        /// </summary>
        void Append(SubsetRef subset)
        {
            var size = subset.Size;
            var required = (long)count + size;
            if (required > rows.Length)
                rows = PooledArray.Grow(rows, count, required);

            var source = subset.Rows;
            if (source is null)
            {
                var span = rows.AsSpan(count, size);
                for (var i = 0; i < span.Length; i++)
                    span[i] = subset.Lo + i;
            }
            else
            {
                Array.Copy(source, subset.Lo, rows, count, size);
            }

            count += size;
        }

    }

}

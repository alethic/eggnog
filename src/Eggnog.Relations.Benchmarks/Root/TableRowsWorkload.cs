using System;
using System.Collections.Generic;

namespace Eggnog.Relations.Benchmarks
{

    /// <summary>
    /// The rows of the table <see cref="ConstrainedScanBenchmarks"/> scans and <see cref="IndexRefreshBenchmarks"/>
    /// indexes: <c>f(x, y) = v</c> with a timestamp, as egglog-bridge lays a binary function out
    /// (<c>[x, y, v, ts]</c>, keyed by <c>(x, y)</c>, sorted by <c>ts</c>), generated from a seed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Row <c>i</c> of <see cref="Count"/> has <c>x</c> uniform in [0, <see cref="XRange"/>) (about eight rows per
    /// value, so a column index on <c>x</c> lists several rows per key), <c>y</c> uniform in [0, <see cref="Count"/>)
    /// with <c>(x, y)</c> distinct, <c>v</c> uniform in [0, <see cref="ValueRange"/>), and the timestamp
    /// <see cref="EpochOf"/>: the rows arrive in <see cref="Epochs"/> equal epochs, so the sort column has that many
    /// runs. Keys are distinct, so loading the rows merges nothing.
    /// </para>
    /// <para>
    /// The tombstone variant deletes every row whose index is 1 mod 4 (<see cref="IsDeleted"/>) after loading: a
    /// quarter of the rows, spread evenly, below the half that would make the table compact.
    /// </para>
    /// </remarks>
    internal sealed class TableRowsWorkload
    {

        /// <summary>
        /// The number of columns: <c>x</c>, <c>y</c>, <c>v</c>, <c>ts</c>.
        /// </summary>
        public const int Arity = 4;

        /// <summary>
        /// The number of key columns: <c>x</c>, <c>y</c>.
        /// </summary>
        public const int KeyCount = 2;

        /// <summary>
        /// The timestamp column, which the table is sorted by.
        /// </summary>
        public const int TimestampColumn = 3;

        /// <summary>
        /// The number of distinct values of <c>v</c>.
        /// </summary>
        public const int ValueRange = 256;

        /// <summary>
        /// The number of epochs the rows arrive in: the timestamps 0 to <c>Epochs - 1</c>.
        /// </summary>
        public const int Epochs = 64;

        /// <summary>
        /// Initializes a new instance over generated cells.
        /// </summary>
        /// <param name="cells">The rows, row-major.</param>
        TableRowsWorkload(uint[] cells)
        {
            Cells = cells;
        }

        /// <summary>
        /// Gets the number of rows.
        /// </summary>
        public int Count => Cells.Length / Arity;

        /// <summary>
        /// Gets the number of distinct values <c>x</c> is drawn from: an eighth of the rows, at least one.
        /// </summary>
        public int XRange => Math.Max(1, Count / 8);

        /// <summary>
        /// Gets the cells of every row, <see cref="Arity"/> per row.
        /// </summary>
        public uint[] Cells { get; }

        /// <summary>
        /// Generates <paramref name="count"/> rows from <paramref name="seed"/>. O(count).
        /// </summary>
        /// <param name="count">The number of rows; at least one.</param>
        /// <param name="seed">The random seed.</param>
        /// <returns>The workload.</returns>
        public static TableRowsWorkload Generate(int count, int seed)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(count, 1);

            var random = new Random(seed);
            var xRange = Math.Max(1, count / 8);
            var cells = new uint[checked(count * Arity)];
            var keys = new HashSet<(uint, uint)>(count);
            for (var i = 0; i < count; i++)
            {
                uint x, y;
                do
                {
                    x = (uint)random.Next(xRange);
                    y = (uint)random.Next(count);
                }
                while (keys.Add((x, y)) == false);

                var offset = i * Arity;
                cells[offset] = x;
                cells[offset + 1] = y;
                cells[offset + 2] = (uint)random.Next(ValueRange);
                cells[offset + 3] = EpochOf(i, count);
            }

            return new TableRowsWorkload(cells);
        }

        /// <summary>
        /// Gets the timestamp of row <paramref name="index"/> of <paramref name="count"/>: its epoch,
        /// <c>index * Epochs / count</c>, nondecreasing in the index.
        /// </summary>
        /// <param name="index">The row index.</param>
        /// <param name="count">The number of rows.</param>
        /// <returns>The timestamp, in [0, <see cref="Epochs"/>).</returns>
        public static uint EpochOf(int index, int count) => (uint)((long)index * Epochs / count);

        /// <summary>
        /// Gets whether the tombstone variant deletes row <paramref name="index"/>: every row whose index is 1 mod 4.
        /// </summary>
        /// <param name="index">The row index.</param>
        /// <returns><c>true</c> if the row is deleted.</returns>
        public static bool IsDeleted(int index) => (index & 3) == 1;

        /// <summary>
        /// Gets row <paramref name="index"/>.
        /// </summary>
        /// <param name="index">The row index.</param>
        /// <returns>The row's cells.</returns>
        public ReadOnlySpan<uint> Row(int index) => new(Cells, index * Arity, Arity);

        /// <summary>
        /// Creates an empty table for these rows: keyed by <c>(x, y)</c>, sorted by <c>ts</c>, nothing to rebuild, and
        /// a merge that keeps the current row (keys are distinct, so it never runs while loading).
        /// </summary>
        /// <param name="parallelism">The table's parallelism.</param>
        /// <returns>The table.</returns>
        public static SortedWritesTable CreateTable(ParallelHeuristics parallelism)
        {
            return new SortedWritesTable(KeyCount, Arity, new ColumnId(TimestampColumn), ReadOnlySpan<ColumnId>.Empty, KeepCurrentMerge.Instance, parallelism);
        }

        /// <summary>
        /// Stages rows [<paramref name="from"/>, <paramref name="to"/>) into <paramref name="table"/> of
        /// <paramref name="database"/> through one buffer, in order, and merges them.
        /// </summary>
        /// <param name="database">The database holding the table.</param>
        /// <param name="table">The table.</param>
        /// <param name="from">The first row.</param>
        /// <param name="to">One past the last row.</param>
        public void Load(Database database, TableId table, int from, int to)
        {
            ArgumentNullException.ThrowIfNull(database);

            var buffer = database.NewBuffer(table);
            for (var i = from; i < to; i++)
                buffer.StageInsert(Row(i));

            buffer.Flush();
            database.MergeAll();
        }

        /// <summary>
        /// Deletes every row <see cref="IsDeleted"/> selects from <paramref name="table"/> of
        /// <paramref name="database"/>, leaving them as tombstones.
        /// </summary>
        /// <param name="database">The database holding the table.</param>
        /// <param name="table">The table, loaded with every row.</param>
        public void DeleteQuarter(Database database, TableId table)
        {
            ArgumentNullException.ThrowIfNull(database);

            var buffer = database.NewBuffer(table);
            for (var i = 0; i < Count; i++)
                if (IsDeleted(i))
                    buffer.StageRemove(Row(i)[..KeyCount]);

            buffer.Flush();
            database.MergeAll();
        }

    }

}

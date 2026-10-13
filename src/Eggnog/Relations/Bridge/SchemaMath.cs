using System;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace Eggnog.Relations
{

    /// <summary>
    /// The physical layout of a function's table: egglog-bridge's <c>SchemaMath</c> (egglog-bridge/src/lib.rs:1533-1618).
    /// A function with <c>n</c> schema columns (keys, then the output) is stored as
    /// <c>[key0, ..., key(n-2), output, timestamp, subsumed?]</c>: the timestamp column is the table's sort column, and the
    /// subsumption column exists only when the function can subsume.
    /// </summary>
    /// <remarks>
    /// Every property is a constant-time computation on two small fields, so callers on hot paths may read them freely or
    /// copy them into locals once.
    /// </remarks>
    internal readonly struct SchemaMath : IEquatable<SchemaMath>
    {

        /// <summary>
        /// The subsumption cell of a subsumed row: egglog's <c>SUBSUMED</c> (lib.rs:1527).
        /// </summary>
        public const uint Subsumed = 1;

        /// <summary>
        /// The subsumption cell of a row that is not subsumed: egglog's <c>NOT_SUBSUMED</c> (lib.rs:1528). Merging two
        /// rows keeps the larger cell (<c>combine_subsumed</c>, lib.rs:1529-1531), so subsumption is sticky.
        /// </summary>
        public const uint NotSubsumed = 0;

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="functionColumns">The number of schema columns, keys and output; at least one.</param>
        /// <param name="subsume">Whether the table has a subsumption column.</param>
        public SchemaMath(int functionColumns, bool subsume)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(functionColumns, 1);
            FunctionColumns = functionColumns;
            Subsume = subsume;
        }

        /// <summary>
        /// Gets the number of schema columns, keys and output. egglog's <c>func_cols</c>.
        /// </summary>
        public int FunctionColumns { get; }

        /// <summary>
        /// Gets whether the table has a subsumption column.
        /// </summary>
        public bool Subsume { get; }

        /// <summary>
        /// Gets the number of key columns. egglog's <c>num_keys</c>.
        /// </summary>
        public int KeyCount
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => FunctionColumns - 1;
        }

        /// <summary>
        /// Gets the number of physical columns. egglog's <c>table_columns</c>.
        /// </summary>
        public int TableColumns
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => FunctionColumns + (Subsume ? 2 : 1);
        }

        /// <summary>
        /// Gets the output column. egglog's <c>ret_val_col</c>.
        /// </summary>
        public int OutputColumn
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => FunctionColumns - 1;
        }

        /// <summary>
        /// Gets the timestamp column, the table's sort column. egglog's <c>ts_col</c>.
        /// </summary>
        public int TimestampColumn
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => FunctionColumns;
        }

        /// <summary>
        /// Gets the subsumption column. egglog's <c>subsume_col</c>.
        /// </summary>
        /// <exception cref="InvalidOperationException">The table cannot subsume.</exception>
        public int SubsumeColumn
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get
            {
                if (Subsume == false)
                    ThrowNoSubsume();

                return FunctionColumns + 1;
            }
        }

        /// <summary>
        /// Gets the subsumption column, or -1 when the table has none: the branch-free form merges use.
        /// </summary>
        public int SubsumeColumnOrNone
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => Subsume ? FunctionColumns + 1 : -1;
        }

        /// <summary>
        /// Fills the columns after the schema columns of a physical row: the timestamp and, when the table has one, the
        /// subsumption cell. egglog's <c>write_table_row</c> with the output already in place.
        /// </summary>
        /// <param name="row">The physical row; <see cref="TableColumns"/> cells, the schema columns already written.</param>
        /// <param name="timestamp">The row's timestamp.</param>
        /// <param name="subsumed">The subsumption cell, ignored when the table has no subsumption column.</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void WriteTail(Span<uint> row, uint timestamp, uint subsumed = NotSubsumed)
        {
            row[FunctionColumns] = timestamp;
            if (Subsume)
                row[FunctionColumns + 1] = subsumed;
        }

        /// <inheritdoc />
        public bool Equals(SchemaMath other) => FunctionColumns == other.FunctionColumns && Subsume == other.Subsume;

        /// <inheritdoc />
        public override bool Equals(object? obj) => obj is SchemaMath other && Equals(other);

        /// <inheritdoc />
        public override int GetHashCode() => HashCode.Combine(FunctionColumns, Subsume);

        /// <inheritdoc />
        public override string ToString() => $"SchemaMath {{ func_cols: {FunctionColumns}, subsume: {Subsume} }}";

        [DoesNotReturn]
        [MethodImpl(MethodImplOptions.NoInlining)]
        static void ThrowNoSubsume()
        {
            throw new InvalidOperationException("The function cannot subsume, so its table has no subsumption column.");
        }

    }

}

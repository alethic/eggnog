using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace Eggnog.Relations
{

    internal sealed partial class ExecutionState
    {

        /// <summary>
        /// The rows this state has staged for missing keys, created on first use. The clone constructor
        /// (Tables/ExecutionState.cs) leaves it null, so a clone starts with an empty cache, as egglog's clones do.
        /// </summary>
        PredictedValues? predicted;

        /// <summary>
        /// Gets this state's predicted-value cache: egglog's <c>predicted</c> field. Shared by
        /// <see cref="LookupOrInsertDefaultInstr"/>, <see cref="PredictRow(TableId, ReadOnlySpan{uint}, ReadOnlySpan{MergeVal}, Span{uint})"/>
        /// and <see cref="PredictColumn(TableId, ReadOnlySpan{uint}, ReadOnlySpan{MergeVal}, ColumnId)"/>.
        /// </summary>
        internal PredictedValues Predicted => predicted ??= new PredictedValues();

        /// <summary>
        /// Gets the number of rows this state has predicted for missing keys.
        /// </summary>
        public int PredictedCount => predicted?.Count ?? 0;

        /// <summary>
        /// Gets the ids of every table the database view sees, in ascending order. egglog's <c>table_ids</c>.
        /// </summary>
        /// <exception cref="InvalidOperationException">The state is not attached to a database.</exception>
        public IEnumerable<TableId> TableIds => RequireView().TableIds;

        /// <summary>
        /// Gets the current row for <paramref name="key"/> in <paramref name="table"/>, or else the row this state
        /// predicts for it, staging <c>key ++ values</c> the first time. egglog's <c>predict_val</c>
        /// (core-relations/src/action/mod.rs:499-556): merge functions and primitives use it to look up or create a term
        /// whose row is not committed yet, and every later prediction of the key through this state (or through
        /// <see cref="LookupOrInsertDefaultInstr"/>) returns the same row.
        /// </summary>
        /// <remarks>
        /// The committed table is read through the database view, so a table the database has taken out (while it
        /// merges or rebuilds) cannot be predicted into, as in egglog. Staging a new row sets <see cref="Changed"/> and
        /// notifies the table. In a child context the committed row is the one the context sees, found by the write-phase
        /// lookup (<see cref="ContextTables.TryGetVisibleRow"/>): one null test at the root.
        /// </remarks>
        /// <param name="table">The table.</param>
        /// <param name="key">The key.</param>
        /// <param name="values">The value columns of a new row: constants, or fresh values from counters.</param>
        /// <param name="row">Receives the row; at least as many cells as the table has columns.</param>
        public void PredictRow(TableId table, ReadOnlySpan<uint> key, ReadOnlySpan<MergeVal> values, Span<uint> row)
        {
            if (layers is { } l)
            {
                PredictRowLayered(l, table, key, values, row);
                return;
            }

            var t = ReadRootTable(table);
            if (row.Length < t.Arity)
                throw new ArgumentException($"The destination needs room for {t.Arity} cells.", nameof(row));

            if (t.TryGetRow(key, row, out _))
                return;

            var p = Predicted;
            var probe = p.GetProbeBuffer(key.Length);
            key.CopyTo(probe);
            var offset = PredictCore(p, t, table, probe, 0, key.Length, values);
            p.Cells.AsSpan(offset, t.Arity).CopyTo(row);
        }

        /// <summary>
        /// Returns a copy of what <see cref="PredictRow(TableId, ReadOnlySpan{uint}, ReadOnlySpan{MergeVal}, Span{uint})"/>
        /// writes. Allocates, as egglog's <c>predict_val</c> returns a vector.
        /// </summary>
        /// <param name="table">The table.</param>
        /// <param name="key">The key.</param>
        /// <param name="values">The value columns of a new row.</param>
        /// <returns>The row.</returns>
        public uint[] PredictRow(TableId table, ReadOnlySpan<uint> key, ReadOnlySpan<MergeVal> values)
        {
            var row = new uint[layers is { } l ? l.Schema.GetSpec(table).Arity : ReadRootTable(table).Arity];
            PredictRow(table, key, values, row);
            return row;
        }

        /// <summary>
        /// Gets column <paramref name="column"/> of what <see cref="PredictRow(TableId, ReadOnlySpan{uint}, ReadOnlySpan{MergeVal}, Span{uint})"/>
        /// would return, without copying the row. egglog's <c>predict_col</c> (core-relations/src/action/mod.rs:558-580).
        /// </summary>
        /// <remarks>
        /// In a child context the committed value is the one the context sees
        /// (<see cref="ContextTables.TryGetVisibleColumn"/>): one null test at the root.
        /// </remarks>
        /// <param name="table">The table.</param>
        /// <param name="key">The key.</param>
        /// <param name="values">The value columns of a new row.</param>
        /// <param name="column">The column.</param>
        /// <returns>The value.</returns>
        public uint PredictColumn(TableId table, ReadOnlySpan<uint> key, ReadOnlySpan<MergeVal> values, ColumnId column)
        {
            if (layers is { } l)
                return PredictColumnLayered(l, table, key, values, column);

            var t = ReadRootTable(table);
            if (t.TryGetRowColumn(key, column, out var value))
                return value;

            var p = Predicted;
            var probe = p.GetProbeBuffer(key.Length);
            key.CopyTo(probe);
            return PredictedColumn(p, t, table, probe, 0, key.Length, values, column);
        }

        /// <summary>
        /// Gets column <paramref name="column"/> of the current or predicted row for the key
        /// <paramref name="key"/>[<paramref name="keyOffset"/>..+<paramref name="keyLength"/>]: the array form of
        /// <see cref="PredictColumn(TableId, ReadOnlySpan{uint}, ReadOnlySpan{MergeVal}, ColumnId)"/> for compiled plans.
        /// </summary>
        /// <param name="table">The table.</param>
        /// <param name="key">The array holding the key.</param>
        /// <param name="keyOffset">The index of the key's first cell.</param>
        /// <param name="keyLength">The number of key cells.</param>
        /// <param name="values">The value columns of a new row.</param>
        /// <param name="column">The column.</param>
        /// <returns>The value.</returns>
        public uint PredictColumn(TableId table, uint[] key, int keyOffset, int keyLength, MergeVal[] values, ColumnId column)
        {
            ArgumentNullException.ThrowIfNull(key);
            ArgumentNullException.ThrowIfNull(values);
            if (layers is { } l)
                return PredictColumnLayered(l, table, key, keyOffset, keyLength, values, column);

            var t = ReadRootTable(table);
            if (t.TryGetRowColumn(new ReadOnlySpan<uint>(key, keyOffset, keyLength), column, out var value))
                return value;

            return PredictedColumn(Predicted, t, table, key, keyOffset, keyLength, values, column);
        }

        /// <summary>
        /// Reads <paramref name="column"/> of the predicted row for a key the committed table <paramref name="t"/> lacks.
        /// </summary>
        uint PredictedColumn(PredictedValues p, Table t, TableId table, uint[] key, int keyOffset, int keyLength, ReadOnlySpan<MergeVal> values, ColumnId column)
        {
            return PredictedColumn(p, t.Spec.KeyCount, t.Arity, table, key, keyOffset, keyLength, values, column);
        }

        /// <summary>
        /// Reads <paramref name="column"/> of the predicted row for a key the committed table lacks, given the table's key
        /// count and arity (from the table at a root, from the store's schema in a child).
        /// </summary>
        uint PredictedColumn(PredictedValues p, int keyCount, int arity, TableId table, uint[] key, int keyOffset, int keyLength, ReadOnlySpan<MergeVal> values, ColumnId column)
        {
            if ((uint)column.Rep >= (uint)arity)
                throw new ArgumentOutOfRangeException(nameof(column), column, $"The table has {arity} columns.");

            var offset = PredictCore(p, keyCount, arity, table, key, keyOffset, keyLength, values);
            return p.Cells[offset + column.Rep];
        }

        /// <summary>
        /// <see cref="PredictCore(PredictedValues, int, int, TableId, uint[], int, int, ReadOnlySpan{MergeVal})"/> for the
        /// committed table <paramref name="t"/>, whose spec is read only here, on a miss.
        /// </summary>
        int PredictCore(PredictedValues p, Table t, TableId table, uint[] key, int keyOffset, int keyLength, ReadOnlySpan<MergeVal> values)
        {
            return PredictCore(p, t.Spec.KeyCount, t.Arity, table, key, keyOffset, keyLength, values);
        }

        /// <summary>
        /// Finds the predicted row for a key the committed table lacks, or builds <c>key ++ values</c>, stages it, sets
        /// <see cref="Changed"/> and records it: egglog's <c>get_val</c> with <c>construct_new_row</c>
        /// (core-relations/src/action/mod.rs:533-556).
        /// </summary>
        /// <returns>The offset of the row in the cache's arena.</returns>
        /// <exception cref="ArgumentException">The key and values do not make a row of the table.</exception>
        int PredictCore(PredictedValues p, int keyCount, int arity, TableId table, uint[] key, int keyOffset, int keyLength, ReadOnlySpan<MergeVal> values)
        {
            // Checked before any id is minted: the row is staged, and a row of the wrong width cannot be.
            if (keyLength != keyCount || keyLength + values.Length != arity)
                throw new ArgumentException($"A row of the table has {keyCount} key and {arity - keyCount} value columns, not {keyLength} and {values.Length}.", nameof(values));

            var offset = p.FindOrBegin(table, key, keyOffset, keyLength, keyLength + values.Length, out var found);
            if (found)
                return offset;

            var cells = p.Cells;
            for (var i = 0; i < values.Length; i++)
            {
                var v = values[i];
                cells[offset + keyLength + i] = v.IsCounter ? ValueFromCounter(IncrementCounter(v.Counter)) : v.Constant.Rep;
            }

            StageInsert(table, new ReadOnlySpan<uint>(cells, offset, keyLength + values.Length));
            p.Commit();
            return offset;
        }

        /// <summary>
        /// Gets the table with id <paramref name="table"/> for reading: the single dispatch point for committed-table
        /// reads. Every caller takes its layered path first, so this throws in a child context; root paths that have
        /// already tested for layers call <see cref="ReadRootTable"/>.
        /// </summary>
        /// <param name="table">The table id.</param>
        /// <returns>The table.</returns>
        /// <exception cref="InvalidOperationException">The state runs in a child context.</exception>
        Table ReadTable(TableId table)
        {
            if (layers is not null)
                return ThrowLayeredRead(table);

            return ReadRootTable(table);
        }

        /// <summary>
        /// Gets the table with id <paramref name="table"/> for reading at a root, in a bare database or over a bare
        /// catalog: through the database view when the state has one (which fails for a table taken out by a merge in
        /// progress, as egglog's <c>db.table_info[table]</c> does), otherwise from the catalog. Small enough to inline
        /// on the root paths that have already tested for layers, so a read there costs one null test.
        /// </summary>
        /// <param name="table">The table id.</param>
        /// <returns>The table.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        Table ReadRootTable(TableId table) => View is { } view ? view.GetTable(table) : catalog[table];

        /// <summary>
        /// Converts a counter value to a cell value: egglog's <c>Value::from_usize</c>, which panics when the value does
        /// not fit in 32 bits.
        /// </summary>
        /// <remarks>
        /// egglog's <c>from_usize</c> (numeric-id/src/lib.rs:528-531, called from core-relations/src/action/mod.rs:547,
        /// 700, 877) accepts <see cref="uint.MaxValue"/>, which is <see cref="Value.StaleRep"/>, the marker of a
        /// deleted row when it lands in a row's first cell. Here a minted id or read counter at or past it is rejected.
        /// </remarks>
        /// <param name="value">The counter value.</param>
        /// <returns>The cell value.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static uint ValueFromCounter(long value)
        {
            if ((ulong)value >= Value.StaleRep)
                ThrowCounterOverflow(value);

            return (uint)value;
        }

        [DoesNotReturn]
        [MethodImpl(MethodImplOptions.NoInlining)]
        static void ThrowCounterOverflow(long value)
        {
            throw new OverflowException($"The counter value {value} does not fit in a 32-bit value below the stale-row marker.");
        }

    }

}

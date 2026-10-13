using System;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace Eggnog.Relations
{

    /// <summary>
    /// A handle for reading and writing one function's table through an <see cref="ExecutionState"/>, holding only ids:
    /// egglog-bridge's <c>TableAction</c> (egglog-bridge/src/lib.rs:1248-1371). Merge functions use it to look up or
    /// mint terms, primitives use it through the <see cref="ActionRegistry"/>, and the frontend uses it to load data and
    /// to fill the scheduler's tables.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Nothing here canonicalizes: keys and rows are used as given. Reads see the committed table; writes are staged
    /// into the state and land at the next merge. Rows written get the current value of the e-graph's timestamp
    /// counter, which at the root equals the epoch the running batch captured.
    /// </para>
    /// <para>
    /// Immutable and stateless, so one instance serves any number of threads, each with its own state. Every method is
    /// one table lookup or one staged row, without allocation (rows of up to 32 columns are built on the stack).
    /// </para>
    /// </remarks>
    internal sealed class TableAction
    {

        /// <summary>
        /// The widest row built on the stack.
        /// </summary>
        const int MaxStackRow = 32;

        readonly TableId table;
        readonly SchemaMath math;
        readonly bool hasDefault;
        readonly MergeVal defaultOutput;
        readonly CounterId timestamp;

        /// <summary>
        /// Initializes a handle for <paramref name="function"/> of <paramref name="egraph"/>. egglog's
        /// <c>TableAction::new</c>.
        /// </summary>
        /// <param name="egraph">The e-graph.</param>
        /// <param name="function">The function.</param>
        public TableAction(EGraph egraph, FunctionId function)
        {
            ArgumentNullException.ThrowIfNull(egraph);
            var info = egraph.GetFunctionInfo(function);
            table = info.Table;
            math = info.Math;
            switch (info.Default.Kind)
            {
                case DefaultValKind.FreshId:
                    hasDefault = true;
                    defaultOutput = MergeVal.FromCounter(egraph.IdCounter);
                    break;
                case DefaultValKind.Const:
                    hasDefault = true;
                    defaultOutput = MergeVal.FromConstant(info.Default.Constant);
                    break;
            }

            timestamp = egraph.TimestampCounter;
        }

        /// <summary>
        /// Gets the function's table.
        /// </summary>
        public TableId Table => table;

        /// <summary>
        /// Gets the layout of the function's table.
        /// </summary>
        public SchemaMath Math => math;

        /// <summary>
        /// Gets whether a missing key is inserted by <see cref="TryLookupOrInsert"/> (the function has a fresh-id or
        /// constant default).
        /// </summary>
        public bool HasDefault => hasDefault;

        /// <summary>
        /// Looks up the output for <paramref name="key"/> in the committed table: egglog's <c>lookup</c>, a pure read
        /// that never inserts, whatever the function's default. One hash probe at a root; in a child context, the row
        /// the child sees (<see cref="ExecutionState.TryLookupColumn"/>): one probe per layer.
        /// </summary>
        /// <param name="state">The execution state.</param>
        /// <param name="key">The key; one cell per key column.</param>
        /// <param name="value">The output, or zero.</param>
        /// <returns><c>true</c> if the key is present.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool TryLookup(ExecutionState state, ReadOnlySpan<uint> key, out uint value)
        {
            return state.TryLookupColumn(table, key, new ColumnId(math.OutputColumn), out value);
        }

        /// <summary>
        /// Gets the number of live rows of the table the state's context sees. egglog's <c>row_count</c>
        /// (<see cref="ExecutionState.VisibleRowCount"/>: the table's live rows at a root, the view's visible rows in a
        /// child).
        /// </summary>
        /// <param name="state">The execution state.</param>
        /// <returns>The row count.</returns>
        public int RowCount(ExecutionState state) => checked((int)state.VisibleRowCount(table));

        /// <summary>
        /// Looks up the output for <paramref name="key"/>, inserting the function's default when the key is missing:
        /// egglog's <c>lookup_or_insert</c>. A constructor mints a fresh id; a function with a constant default inserts
        /// the constant; a function without a default behaves as <see cref="TryLookup"/>. The inserted row is
        /// <c>key ++ [default, timestamp, not subsumed]</c>, staged once per state: later calls through the same state
        /// return the same output (the state's predicted values, egglog's <c>predict_val</c>).
        /// </summary>
        /// <remarks>A write: only for action contexts (egglog issue #772).</remarks>
        /// <param name="state">The execution state.</param>
        /// <param name="key">The key; one cell per key column.</param>
        /// <param name="value">The output, or zero.</param>
        /// <returns><c>false</c> only when the key is missing and the function has no default.</returns>
        public bool TryLookupOrInsert(ExecutionState state, ReadOnlySpan<uint> key, out uint value)
        {
            if (hasDefault == false)
                return TryLookup(state, key, out value);

            // The value columns of a new row: output, timestamp and (if any) subsumption, in column order (egglog writes
            // them with a SchemaMath whose func_cols is 1, lib.rs:1310-1324).
            Span<MergeVal> values = stackalloc MergeVal[3];
            values[0] = defaultOutput;
            values[1] = MergeVal.FromConstant(new Value(ReadTimestamp(state)));
            values[2] = MergeVal.FromConstant(new Value(SchemaMath.NotSubsumed));
            var count = math.Subsume ? 3 : 2;
            value = state.PredictColumn(table, key, values.Slice(0, count), new ColumnId(math.OutputColumn));
            return true;
        }

        /// <summary>
        /// Stages the insertion of <paramref name="row"/>, extended with the current timestamp and, when the table has
        /// one, a cleared subsumption cell: egglog's <c>insert</c>. Conflicts with an existing output go through the
        /// function's merge when the table merges.
        /// </summary>
        /// <param name="state">The execution state.</param>
        /// <param name="row">The keys and the output.</param>
        /// <exception cref="ArgumentException"><paramref name="row"/> does not have one cell per schema column.</exception>
        public void Insert(ExecutionState state, ReadOnlySpan<uint> row)
        {
            if (row.Length != math.FunctionColumns)
                throw new ArgumentException($"A row of the function has {math.FunctionColumns} columns, not {row.Length}.", nameof(row));

            var columns = math.TableColumns;
            Span<uint> full = columns <= MaxStackRow ? stackalloc uint[MaxStackRow] : new uint[columns];
            full = full.Slice(0, columns);
            row.CopyTo(full);
            math.WriteTail(full, ReadTimestamp(state));
            state.StageInsert(table, full);
        }

        /// <summary>
        /// Stages the removal of the row with key <paramref name="key"/>: egglog's <c>remove</c>.
        /// </summary>
        /// <param name="state">The execution state.</param>
        /// <param name="key">The key; one cell per key column.</param>
        public void Remove(ExecutionState state, ReadOnlySpan<uint> key) => state.StageRemove(table, key);

        /// <summary>
        /// Stages a subsumed copy of the row with key <paramref name="key"/>, which the subsumption merge (the maximum)
        /// then marks subsumed: egglog's <c>subsume</c>. The output is read from the committed table.
        /// </summary>
        /// <param name="state">The execution state.</param>
        /// <param name="key">The key; one cell per key column.</param>
        /// <exception cref="InvalidOperationException">The function cannot subsume, or the table holds no row for the key
        /// (egglog panics with "subsume lookup failed", lib.rs:1359).</exception>
        public void Subsume(ExecutionState state, ReadOnlySpan<uint> key)
        {
            var subsumeColumn = math.SubsumeColumn;
            if (key.Length != math.KeyCount)
                throw new ArgumentException($"A key of the function has {math.KeyCount} columns, not {key.Length}.", nameof(key));

            if (TryLookup(state, key, out var output) == false)
                ThrowSubsumeLookupFailed();

            var columns = math.TableColumns;
            Span<uint> full = columns <= MaxStackRow ? stackalloc uint[MaxStackRow] : new uint[columns];
            full = full.Slice(0, columns);
            key.CopyTo(full);
            full[math.OutputColumn] = output;
            full[math.TimestampColumn] = ReadTimestamp(state);
            full[subsumeColumn] = SchemaMath.Subsumed;
            state.StageInsert(table, full);
        }

        /// <summary>
        /// Reads the e-graph's timestamp counter: egglog's <c>Value::from_usize(state.read_counter(self.timestamp))</c>,
        /// which panics on overflow.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        uint ReadTimestamp(ExecutionState state) => checked((uint)state.ReadCounter(timestamp));

        [DoesNotReturn]
        [MethodImpl(MethodImplOptions.NoInlining)]
        static void ThrowSubsumeLookupFailed()
        {
            throw new InvalidOperationException("subsume lookup failed: the table holds no row for the key.");
        }

    }

}

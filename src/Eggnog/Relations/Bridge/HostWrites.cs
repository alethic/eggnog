using System;

namespace Eggnog.Relations
{

    /// <summary>
    /// The writes a host staged in one <see cref="EGraphContext"/> and has not flushed yet: logical function rows (keys
    /// and output, without the timestamp and subsumption cells) and union pairs, kept on the bridge side until
    /// <see cref="EGraphContext.FlushUpdates"/> replays them into a state stamped with the flush's epoch, after the
    /// context caught up with its parent.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Why bridge-side: a buffer from <c>Database.NewBuffer</c> notifies its table when it is created, so a merge
    /// between the staging and the flush would consume the notification; and a row stamped at staging could carry an
    /// epoch below a later catch-up. Replaying at the flush stamps every row with the flush's epoch by construction.
    /// </para>
    /// <para>
    /// Each row remembers whether its id cells are canonicalized at the replay in the running context: rows staged
    /// through the context API are, at every level, the root included, so every id the rule layer writes is canonical
    /// in the target context; a row staged as given is replayed as given (a child canonicalizes it at its merge
    /// anyway). The e-graph's egglog-compatible root API does not stage here: its rows go straight into database
    /// buffers (<see cref="EGraphContext.AddValuesAtRoot"/>). Each row also remembers whether it is replayed subsumed:
    /// a host subsumption is a subsumed copy of the row, which the function's merge makes sticky (egglog's
    /// <c>subsume</c>).
    /// </para>
    /// <para>
    /// Storage: one arena of cells and four parallel arrays per row, grown geometrically. Kept across flushes while
    /// small (up to <see cref="RetainedCells"/> cells and <see cref="RetainedRows"/> rows), so a warm context stages
    /// its usual batches without allocating; released by the flush after a larger batch, so a context that lives long
    /// (the root) keeps nothing proportional to its largest batch. Not thread-safe: the owning context's domain thread
    /// only.
    /// </para>
    /// </remarks>
    internal sealed class HostWrites
    {

        /// <summary>
        /// The widest row replayed through a stack buffer.
        /// </summary>
        const int MaxStackRow = 32;

        /// <summary>
        /// The most cells the arena keeps across a flush; a larger arena is released when cleared.
        /// </summary>
        internal const int RetainedCells = 4096;

        /// <summary>
        /// The most row slots the per-row arrays keep across a flush; larger ones are released when cleared.
        /// </summary>
        internal const int RetainedRows = 1024;

        /// <summary>
        /// The most union slots kept across a flush; a larger array is released when cleared.
        /// </summary>
        internal const int RetainedUnions = 1024;

        uint[] cells = Array.Empty<uint>();
        int cellCount;
        FunctionId[] functions = Array.Empty<FunctionId>();
        int[] offsets = Array.Empty<int>();
        bool[] canonicalize = Array.Empty<bool>();
        bool[] subsumed = Array.Empty<bool>();
        int rowCount;
        uint[] unions = Array.Empty<uint>();
        int unionCount;

        /// <summary>
        /// Gets whether nothing is staged.
        /// </summary>
        public bool IsEmpty => rowCount == 0 && unionCount == 0;

        /// <summary>
        /// Gets the number of staged rows.
        /// </summary>
        public int RowCount => rowCount;

        /// <summary>
        /// Gets the number of staged unions.
        /// </summary>
        public int UnionCount => unionCount;

        /// <summary>
        /// Stages a row of <paramref name="function"/>: its keys and output, one cell per schema column. The caller has
        /// checked the row's width. O(row) amortized.
        /// </summary>
        /// <param name="function">The function.</param>
        /// <param name="row">The keys and output; copied.</param>
        /// <param name="canonicalizeIds">Whether the row's id cells are canonicalized when it is replayed (the context
        /// API); otherwise it is replayed as given.</param>
        /// <param name="subsume">Whether the row is replayed subsumed; only for a function that can subsume, which the
        /// caller has checked.</param>
        public void AddRow(FunctionId function, ReadOnlySpan<uint> row, bool canonicalizeIds, bool subsume = false)
        {
            if (rowCount == functions.Length)
            {
                var size = Math.Max(8, functions.Length * 2);
                Array.Resize(ref functions, size);
                Array.Resize(ref offsets, size);
                Array.Resize(ref canonicalize, size);
                Array.Resize(ref subsumed, size);
            }

            if (cellCount + row.Length > cells.Length)
                Array.Resize(ref cells, Math.Max(cellCount + row.Length, Math.Max(32, cells.Length * 2)));

            row.CopyTo(cells.AsSpan(cellCount));
            functions[rowCount] = function;
            offsets[rowCount] = cellCount;
            canonicalize[rowCount] = canonicalizeIds;
            subsumed[rowCount] = subsume;
            rowCount++;
            cellCount += row.Length;
        }

        /// <summary>
        /// Stages the union of <paramref name="a"/> and <paramref name="b"/>. O(1) amortized.
        /// </summary>
        /// <param name="a">One id.</param>
        /// <param name="b">The other.</param>
        public void AddUnion(uint a, uint b)
        {
            if (2 * unionCount + 2 > unions.Length)
                Array.Resize(ref unions, Math.Max(16, unions.Length * 2));

            unions[2 * unionCount] = a;
            unions[2 * unionCount + 1] = b;
            unionCount++;
        }

        /// <summary>
        /// Stages every row and union into <paramref name="state"/>, in staging order, stamped with
        /// <paramref name="timestamp"/> and subsumed as staged: rows through their function's table, unions through the
        /// union-find. The id cells of rows staged through the context API are canonicalized with
        /// <paramref name="finder"/>, which is current once the context prepared for the write. Does not clear.
        /// </summary>
        /// <remarks>O(staged cells) plus one find per id cell of a canonicalized row; no allocation for rows of up to 32
        /// columns.</remarks>
        /// <param name="state">The flush's state.</param>
        /// <param name="timestamp">The flush's epoch.</param>
        /// <param name="graph">The e-graph, for the functions' layouts.</param>
        /// <param name="finder">The running context's find.</param>
        public void ReplayInto(ExecutionState state, uint timestamp, EGraph graph, LayeredUnionFind finder)
        {
            ArgumentNullException.ThrowIfNull(state);
            ArgumentNullException.ThrowIfNull(graph);
            ArgumentNullException.ThrowIfNull(finder);
            Span<uint> stack = stackalloc uint[MaxStackRow];
            for (var i = 0; i < rowCount; i++)
            {
                var info = graph.GetFunctionInfo(functions[i]);
                var math = info.Math;
                var columns = math.TableColumns;
                var full = columns <= MaxStackRow ? stack.Slice(0, columns) : new uint[columns];
                cells.AsSpan(offsets[i], math.FunctionColumns).CopyTo(full);
                if (canonicalize[i])
                {
                    var schema = info.Schema;
                    for (var c = 0; c < schema.Length; c++)
                        if (schema[c].IsId)
                            full[c] = finder.Find(full[c]);
                }

                math.WriteTail(full, timestamp, subsumed[i] ? SchemaMath.Subsumed : SchemaMath.NotSubsumed);
                state.StageInsert(info.Table, full);
            }

            if (unionCount == 0)
                return;

            Span<uint> union = stackalloc uint[3];
            union[2] = timestamp;
            var unionFind = graph.UnionFindTable;
            for (var i = 0; i < unionCount; i++)
            {
                union[0] = unions[2 * i];
                union[1] = unions[2 * i + 1];
                state.StageInsert(unionFind, union);
            }
        }

        /// <summary>
        /// Drops the rows staged from position <paramref name="rows"/> on (a <see cref="RowCount"/> read earlier), keeping
        /// the unions: undoes a batch that failed validation part-way. O(1).
        /// </summary>
        /// <param name="rows">The number of rows to keep.</param>
        public void TruncateRows(int rows)
        {
            if ((uint)rows > (uint)rowCount)
                throw new ArgumentOutOfRangeException(nameof(rows));

            if (rows < rowCount)
            {
                cellCount = offsets[rows];
                rowCount = rows;
            }
        }

        /// <summary>
        /// Drops every row staged for <paramref name="function"/>, keeping the others in staging order, and the unions: a
        /// clear of the function issued after them (egglog's <c>clear_table</c> drops what in-flight buffers staged for the
        /// table, lib.rs:409-424). O(staged cells).
        /// </summary>
        /// <param name="function">The function.</param>
        /// <returns>The number of rows dropped.</returns>
        public int RemoveRows(FunctionId function)
        {
            var kept = 0;
            var keptCells = 0;
            for (var i = 0; i < rowCount; i++)
            {
                var start = offsets[i];
                var end = i + 1 < rowCount ? offsets[i + 1] : cellCount;
                if (functions[i] == function)
                    continue;

                if (kept != i)
                {
                    Array.Copy(cells, start, cells, keptCells, end - start);
                    functions[kept] = functions[i];
                    canonicalize[kept] = canonicalize[i];
                    subsumed[kept] = subsumed[i];
                }

                offsets[kept] = keptCells;
                keptCells += end - start;
                kept++;
            }

            var dropped = rowCount - kept;
            rowCount = kept;
            cellCount = keptCells;
            return dropped;
        }

        /// <summary>
        /// Drops everything staged. Storage within the retention limits is kept for the next batch; larger arrays are
        /// released, so a bulk load leaves nothing behind. O(1).
        /// </summary>
        public void Clear()
        {
            rowCount = 0;
            cellCount = 0;
            unionCount = 0;
            if (cells.Length > RetainedCells)
                cells = Array.Empty<uint>();

            if (functions.Length > RetainedRows)
            {
                functions = Array.Empty<FunctionId>();
                offsets = Array.Empty<int>();
                canonicalize = Array.Empty<bool>();
                subsumed = Array.Empty<bool>();
            }

            if (unions.Length > 2 * RetainedUnions)
                unions = Array.Empty<uint>();
        }

        /// <summary>
        /// Drops everything staged and the storage: the context is disposed.
        /// </summary>
        public void Release()
        {
            Clear();
            cells = Array.Empty<uint>();
            functions = Array.Empty<FunctionId>();
            offsets = Array.Empty<int>();
            canonicalize = Array.Empty<bool>();
            subsumed = Array.Empty<bool>();
            unions = Array.Empty<uint>();
        }

    }

}

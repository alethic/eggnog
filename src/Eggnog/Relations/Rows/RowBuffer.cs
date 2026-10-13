using System;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Eggnog.Relations
{

    /// <summary>
    /// A batch of rows of one arity, stored row-major in a single <see cref="uint"/> array: egglog's
    /// <c>RowBuffer</c> (core-relations/src/row_buffer/mod.rs:107-386). Tables keep their rows in one, and mutation
    /// buffers, staged merge outputs and materialized subsets use them as scratch.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Layout: row <c>r</c> occupies <c>Data[r * Arity .. (r + 1) * Arity)</c>; only the first
    /// <c>Count * Arity</c> cells are meaningful. Row ids are dense, <c>0 .. Count</c>, and stay stable until
    /// <see cref="RemoveStale{TRemap}(ref TRemap)"/>.
    /// </para>
    /// <para>
    /// Deletion is by tombstone: a row whose first cell is <see cref="Value.StaleRep"/> is stale. Stale rows stay in
    /// place (<see cref="EnumerateRows"/> yields them, <see cref="EnumerateNonStale"/> skips them) until compaction.
    /// </para>
    /// <para>
    /// The arity is at least one; egglog asserts it (mod.rs:246-249). Arity-zero rows (empty keys) go in an
    /// <see cref="ArbitraryRowBuffer"/>.
    /// </para>
    /// <para>
    /// Storage of more than 256 cells (and at most 32 MiB) is rented from
    /// <see cref="System.Buffers.ArrayPool{T}.Shared"/> and goes back only on <see cref="Dispose"/>; see
    /// <see cref="PooledArray"/>. Growth doubles and leaves the old array to the garbage collector, as Rust's
    /// <c>Vec</c> growth frees it to the allocator, so an enumerator, span or <see cref="Data"/> reference taken
    /// before a growth keeps reading the old contents (writes through it no longer reach the buffer) and never sees
    /// recycled storage. Growth, <see cref="ParallelWriter"/> and <see cref="Dispose"/> replace <see cref="Data"/>, so
    /// callers re-read it after them. Using spans, enumerators or <see cref="Data"/> references after
    /// <see cref="Dispose"/> or <see cref="ParallelWriter"/> is a use-after-free: the storage belongs to the pool or
    /// to the writer by then.
    /// </para>
    /// <para>
    /// Not thread-safe for writers. Concurrent readers are fine, and concurrent <see cref="SetStaleShared"/> calls on
    /// distinct rows are fine (see there).
    /// </para>
    /// </remarks>
    internal sealed class RowBuffer : IDisposable
    {

        readonly int arity;
        uint[] data;
        int count;

        /// <summary>
        /// Initializes an empty buffer for rows of <paramref name="arity"/> cells. Allocates nothing until the first
        /// row.
        /// </summary>
        /// <param name="arity">The number of cells per row; at least one.</param>
        public RowBuffer(int arity)
        {
            if (arity < 1)
                throw new ArgumentOutOfRangeException(nameof(arity), arity, "A row buffer needs at least one column; use ArbitraryRowBuffer for arity zero.");

            this.arity = arity;
            data = Array.Empty<uint>();
        }

        /// <summary>
        /// Initializes an empty buffer for rows of <paramref name="arity"/> cells with room for
        /// <paramref name="capacity"/> rows.
        /// </summary>
        /// <param name="arity">The number of cells per row; at least one.</param>
        /// <param name="capacity">The number of rows to reserve.</param>
        public RowBuffer(int arity, int capacity)
            : this(arity)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(capacity);

            if (capacity > 0)
                Reserve(capacity);
        }

        /// <summary>
        /// Initializes a buffer that takes ownership of <paramref name="data"/>, whose first
        /// <paramref name="count"/> rows are valid.
        /// </summary>
        /// <param name="arity">The number of cells per row; at least one.</param>
        /// <param name="data">The storage, which the buffer now owns.</param>
        /// <param name="count">The number of valid rows.</param>
        internal RowBuffer(int arity, uint[] data, int count)
        {
            Debug.Assert(arity >= 1);
            Debug.Assert(count >= 0 && (long)count * arity <= data.Length);

            this.arity = arity;
            this.data = data;
            this.count = count;
        }

        /// <summary>
        /// Gets the number of cells per row.
        /// </summary>
        public int Arity
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => arity;
        }

        /// <summary>
        /// Gets the number of rows, stale ones included. egglog's <c>len</c>.
        /// </summary>
        public int Count
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => count;
        }

        /// <summary>
        /// Gets whether the buffer holds no rows.
        /// </summary>
        public bool IsEmpty => count == 0;

        /// <summary>
        /// Gets the number of rows the current storage holds without growing.
        /// </summary>
        public int Capacity => data.Length / arity;

        /// <summary>
        /// Gets the number of meaningful cells: <c>Count * Arity</c>.
        /// </summary>
        public int CellCount => count * arity;

        /// <summary>
        /// Gets the backing array, for code that indexes rows directly (compiled plans, bulk copies). Row <c>r</c>
        /// starts at <c>r * Arity</c>; cells at or past <see cref="CellCount"/> are garbage. The array is replaced
        /// whenever the buffer grows, hands its storage to a parallel writer, or is disposed, so read this again after
        /// any such call.
        /// </summary>
        public uint[] Data
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => data;
        }

        /// <summary>
        /// Removes every row, keeping the storage. O(1).
        /// </summary>
        public void Clear()
        {
            count = 0;
        }

        /// <summary>
        /// Appends a row and returns its id. Amortized O(<see cref="Arity"/>). egglog's <c>add_row</c>.
        /// </summary>
        /// <param name="row">The row's cells; exactly <see cref="Arity"/> of them. May be a row of this buffer.</param>
        /// <returns>The new row's id: the old <see cref="Count"/>.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int AddRow(ReadOnlySpan<uint> row)
        {
            if (row.Length != arity)
                ThrowArityMismatch(row.Length);

            var start = count * arity;
            if (data.Length - start < arity)
                return AddRowGrowing(row);

            RowCells.Copy(ref MemoryMarshal.GetReference(row), ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(data), start), arity);
            return count++;
        }

        /// <summary>
        /// Appends the row whose cells start at <paramref name="source"/>[<paramref name="offset"/>]. The array-based
        /// form of <see cref="AddRow(ReadOnlySpan{uint})"/> for compiled plans, which cannot use spans.
        /// </summary>
        /// <param name="source">An array holding the row; may be this buffer's <see cref="Data"/>.</param>
        /// <param name="offset">The index of the row's first cell.</param>
        /// <returns>The new row's id.</returns>
        public int AddRow(uint[] source, int offset)
        {
            ArgumentNullException.ThrowIfNull(source);
            if (offset < 0 || source.Length - offset < arity)
                ThrowSourceOutOfRange();

            return AddRow(new ReadOnlySpan<uint>(source, offset, arity));
        }

        /// <summary>
        /// Gets the cells of row <paramref name="row"/>. O(1). egglog's <c>get_row</c>.
        /// </summary>
        /// <param name="row">A row id below <see cref="Count"/>.</param>
        /// <returns>The row's cells.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ReadOnlySpan<uint> GetRow(int row)
        {
            if ((uint)row >= (uint)count)
                ThrowRowOutOfRange(row);

            return MemoryMarshal.CreateReadOnlySpan(ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(data), row * arity), arity);
        }

        /// <summary>
        /// Gets the cells of row <paramref name="row"/> for writing. O(1). egglog's <c>get_row_mut</c>.
        /// </summary>
        /// <param name="row">A row id below <see cref="Count"/>.</param>
        /// <returns>The row's cells.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public Span<uint> GetMutableRow(int row)
        {
            if ((uint)row >= (uint)count)
                ThrowRowOutOfRange(row);

            return MemoryMarshal.CreateSpan(ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(data), row * arity), arity);
        }

        /// <summary>
        /// Gets the cells of row <paramref name="row"/> without a bounds check. egglog's <c>get_row_unchecked</c>,
        /// for hash probes that only follow row ids the table itself stored. The caller guarantees
        /// <c>0 &lt;= row &lt; Count</c>; debug builds assert it.
        /// </summary>
        /// <param name="row">A row id below <see cref="Count"/>.</param>
        /// <returns>The row's cells.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal ReadOnlySpan<uint> GetRowUnchecked(int row)
        {
            Debug.Assert((uint)row < (uint)count);
            return MemoryMarshal.CreateReadOnlySpan(ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(data), row * arity), arity);
        }

        /// <summary>
        /// Gets the index in <see cref="Data"/> of the first cell of row <paramref name="row"/>, checking the row id.
        /// For compiled plans, which read <c>Data[RowOffset(row) + column]</c>.
        /// </summary>
        /// <param name="row">A row id below <see cref="Count"/>.</param>
        /// <returns><c>row * Arity</c>.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int RowOffset(int row)
        {
            if ((uint)row >= (uint)count)
                ThrowRowOutOfRange(row);

            return row * arity;
        }

        /// <summary>
        /// Gets whether row <paramref name="row"/> is stale. O(1).
        /// </summary>
        /// <param name="row">A row id below <see cref="Count"/>.</param>
        /// <returns><c>true</c> if its first cell is <see cref="Value.StaleRep"/>.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool IsStale(int row)
        {
            if ((uint)row >= (uint)count)
                ThrowRowOutOfRange(row);

            return Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(data), row * arity) == Value.StaleRep;
        }

        /// <summary>
        /// Marks row <paramref name="row"/> stale by writing <see cref="Value.StaleRep"/> to its first cell.
        /// Idempotent. O(1). egglog's <c>set_stale</c>.
        /// </summary>
        /// <param name="row">A row id below <see cref="Count"/>.</param>
        /// <returns>Whether the row was already stale.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool SetStale(int row)
        {
            if ((uint)row >= (uint)count)
                ThrowRowOutOfRange(row);

            ref var first = ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(data), row * arity);
            var was = first == Value.StaleRep;
            first = Value.StaleRep;
            return was;
        }

        /// <summary>
        /// Marks row <paramref name="row"/> stale while other threads use the buffer: egglog's
        /// <c>set_stale_shared</c>, used by parallel deletes and merges, where each thread owns the rows of its hash
        /// shard. The caller guarantees that no other thread reads or writes <paramref name="row"/> concurrently;
        /// threads may mark distinct rows at the same time. Visibility to other threads comes from the barrier that
        /// ends the parallel phase.
        /// </summary>
        /// <remarks>
        /// egglog needs <c>Cell</c> storage and an unsafe method to write through a shared reference. A plain
        /// aligned 32-bit store to a distinct array element is already race-free in .NET, so this is the same store
        /// as <see cref="SetStale"/>; it exists to name the contract at the call site.
        /// </remarks>
        /// <param name="row">A row id below <see cref="Count"/>.</param>
        /// <returns>Whether the row was already stale.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool SetStaleShared(int row)
        {
            return SetStale(row);
        }

        /// <summary>
        /// Removes stale rows, moving live rows down so that ids are dense again; relative order is kept.
        /// Invalidates existing row ids. O(<see cref="CellCount"/>), in place.
        /// </summary>
        public void RemoveStale()
        {
            var none = default(NoRowRemap);
            RemoveStale(ref none);
        }

        /// <summary>
        /// Removes stale rows, moving live rows down so that ids are dense again, and reports every live row's old
        /// and new id to <paramref name="remap"/> in increasing order (live rows that do not move included).
        /// Invalidates existing row ids. egglog's <c>remove_stale</c> (mod.rs:354-385). O(<see cref="CellCount"/>), in
        /// place, one pass; each run of consecutive live rows moves with a single copy.
        /// </summary>
        /// <typeparam name="TRemap">The callback type; a struct, so the compaction is specialized for it.</typeparam>
        /// <param name="remap">The callback.</param>
        public void RemoveStale<TRemap>(ref TRemap remap)
            where TRemap : struct, IRowRemap
        {
            var n = count;
            var a = arity;

            // A single bounds-checked span covers every cell touched below, so the loops index without checks.
            ref var cells = ref MemoryMarshal.GetReference(data.AsSpan(0, n * a));

            // Live rows before the first stale one keep their ids and stay where they are.
            var read = 0;
            while (read < n && Unsafe.Add(ref cells, read * a) != Value.StaleRep)
            {
                remap.Remap(MemoryMarshal.CreateReadOnlySpan(ref Unsafe.Add(ref cells, read * a), a), read, read);
                read++;
            }

            var write = read;
            while (read < n)
            {
                // Skip a run of stale rows, then move the run of live rows behind it down to `write` in one copy.
                while (read < n && Unsafe.Add(ref cells, read * a) == Value.StaleRep)
                    read++;

                var start = read;
                while (read < n && Unsafe.Add(ref cells, read * a) != Value.StaleRep)
                    read++;

                var length = read - start;
                if (length == 0)
                    break;

                // The ranges may overlap (destination below source); span copies handle that like memmove.
                MemoryMarshal.CreateReadOnlySpan(ref Unsafe.Add(ref cells, start * a), length * a)
                    .CopyTo(MemoryMarshal.CreateSpan(ref Unsafe.Add(ref cells, write * a), length * a));

                for (var i = 0; i < length; i++)
                    remap.Remap(MemoryMarshal.CreateReadOnlySpan(ref Unsafe.Add(ref cells, (write + i) * a), a), start + i, write + i);

                write += length;
            }

            count = write;
        }

        /// <summary>
        /// Ensures room for <paramref name="additionalRows"/> more rows without growing. Amortized like
        /// <c>Vec::reserve</c>: the storage at least doubles when it grows. egglog's <c>reserve</c>.
        /// </summary>
        /// <param name="additionalRows">The number of rows to make room for.</param>
        public void Reserve(int additionalRows)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(additionalRows);

            var required = ((long)count + additionalRows) * arity;
            if (required > data.Length)
                Grow(required);
        }

        /// <summary>
        /// Sets the number of rows without touching the cells: egglog's <c>set_len</c>. For code that fills
        /// <see cref="Data"/> directly (after <see cref="Reserve"/>), possibly from several threads writing disjoint
        /// rows, and then publishes the rows at once. Rows past the old count must have been written, or they read as
        /// whatever the storage held.
        /// </summary>
        /// <param name="rows">The new row count; the storage must hold that many rows.</param>
        public void SetCount(int rows)
        {
            if (rows < 0 || (long)rows * arity > data.Length)
                throw new ArgumentOutOfRangeException(nameof(rows), rows, "The row count exceeds the buffer's capacity.");

            count = rows;
        }

        /// <summary>
        /// Enumerates every row, stale ones included, in id order. egglog's <c>iter</c>. The enumerator captures the
        /// storage and row count: rows added while it runs are not visited, and after a growth it reads (and writes)
        /// the old storage. Do not dispose the buffer while enumerating.
        /// </summary>
        /// <returns>The enumerator.</returns>
        public RowBufferEnumerator EnumerateRows()
        {
            return new RowBufferEnumerator(data, arity, count);
        }

        /// <summary>
        /// Enumerates the rows that are not stale, in id order. egglog's <c>non_stale</c> and <c>non_stale_mut</c>.
        /// Captures the storage and row count like <see cref="EnumerateRows"/>.
        /// </summary>
        /// <returns>The enumerator.</returns>
        public RowBufferNonStaleEnumerator EnumerateNonStale()
        {
            return new RowBufferNonStaleEnumerator(data, arity, count);
        }

        /// <summary>
        /// Copies the buffer: same arity, same rows (stale ones included), fresh storage sized to fit. egglog's
        /// <c>Clone for RowBuffer</c>. O(<see cref="CellCount"/>).
        /// </summary>
        /// <returns>The copy, which the caller owns.</returns>
        public RowBuffer Clone()
        {
            var cells = count * arity;
            var copy = PooledArray.Allocate<uint>(cells);
            data.AsSpan(0, cells).CopyTo(copy);
            return new RowBuffer(arity, copy, count);
        }

        /// <summary>
        /// Moves the rows into a writer that many threads can append to at once, leaving this buffer empty. egglog's
        /// <c>parallel_writer</c>. Call <see cref="Reserve"/> first with the most rows the writers may append, so that
        /// they never have to grow the storage. <see cref="ParallelRowBufferWriter.Finish"/> returns the result.
        /// </summary>
        /// <returns>The writer, which owns the storage until it finishes.</returns>
        internal ParallelRowBufferWriter ParallelWriter()
        {
            var writer = new ParallelRowBufferWriter(arity, data, count);
            data = Array.Empty<uint>();
            count = 0;
            return writer;
        }

        /// <summary>
        /// Returns the storage to the shared array pool and empties the buffer. The buffer stays usable and allocates
        /// again on the next row; disposing twice is harmless. Rust drops the pooled vector instead. Spans,
        /// enumerators and <see cref="Data"/> references taken before must not be used afterwards: the pool may
        /// already have handed the storage to someone else.
        /// </summary>
        public void Dispose()
        {
            var old = data;
            data = Array.Empty<uint>();
            count = 0;
            PooledArray.Release(old);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        int AddRowGrowing(ReadOnlySpan<uint> row)
        {
            // The row may live in the old storage; that storage is left to the garbage collector rather than returned
            // to the pool (see PooledArray.Grow), so it stays intact for the copy and for anyone still reading it.
            var start = count * arity;
            var next = GrownCopy((long)start + arity);
            RowCells.Copy(ref MemoryMarshal.GetReference(row), ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(next), start), arity);
            data = next;
            return count++;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        void Grow(long requiredCells)
        {
            data = GrownCopy(requiredCells);
        }

        /// <summary>
        /// Allocates larger storage holding the current rows. The old array is not released: growth drops it to the
        /// garbage collector, as <c>Vec</c> growth frees it to the allocator rather than to egglog's pool.
        /// </summary>
        uint[] GrownCopy(long requiredCells)
        {
            if (requiredCells > Array.MaxLength)
                throw new InvalidOperationException($"A row buffer of arity {arity} cannot hold more than {Array.MaxLength / arity} rows.");

            return PooledArray.Grow(data, count * arity, requiredCells);
        }

        [DoesNotReturn]
        [MethodImpl(MethodImplOptions.NoInlining)]
        void ThrowArityMismatch(int length)
        {
            throw new ArgumentException($"Expected a row of {arity} cells but got {length}.", "row");
        }

        [DoesNotReturn]
        [MethodImpl(MethodImplOptions.NoInlining)]
        void ThrowRowOutOfRange(int row)
        {
            throw new ArgumentOutOfRangeException(nameof(row), row, $"The buffer holds {count} rows.");
        }

        [DoesNotReturn]
        [MethodImpl(MethodImplOptions.NoInlining)]
        static void ThrowSourceOutOfRange()
        {
            throw new ArgumentOutOfRangeException("offset", "The source array does not hold a whole row at the offset.");
        }

    }

}

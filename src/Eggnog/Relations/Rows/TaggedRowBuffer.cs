using System;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Eggnog.Relations
{

    /// <summary>
    /// A batch of rows each tagged with the id of the row it came from: egglog's <c>TaggedRowBuffer</c>
    /// (core-relations/src/row_buffer/mod.rs:388-521). Scans write their output into one (it is the
    /// <see cref="IRowSink"/> egglog uses), and rebuilds and index builds use them to materialize subsets of a table.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Layout: like a <see cref="RowBuffer"/> of arity <see cref="Arity"/> + 1, row-major in one <see cref="uint"/>
    /// array, with the source row id in the last cell of each row. Row <c>r</c> occupies
    /// <c>Data[r * Stride .. (r + 1) * Stride)</c>, cells first and tag last. The buffer's own row ids are dense
    /// insertion indices. A row is stale when its first physical cell is <see cref="Value.StaleRep"/>; with arity
    /// zero that cell is the tag, as in egglog.
    /// </para>
    /// <para>
    /// egglog has a pooled variant and an inline one (<c>SmallVec</c> with 64 cells on the stack) behind a type
    /// parameter. Here there is one type: storage of more than 256 cells (and at most 32 MiB) is rented from the shared
    /// array pool and returned by <see cref="Dispose"/>, smaller storage is an ordinary allocation, and
    /// <see cref="CreateInline"/> makes its first allocation the 64 cells egglog keeps inline. Neither allocates
    /// storage before the first row. Growth drops the old storage to the garbage collector rather than recycling it,
    /// so enumerators and spans taken before a growth keep reading the old rows (see <see cref="RowBuffer"/>).
    /// </para>
    /// <para>
    /// The same threading rules as <see cref="RowBuffer"/>: one writer, or many readers.
    /// </para>
    /// </remarks>
    internal sealed class TaggedRowBuffer : IRowSink, IDisposable
    {

        /// <summary>
        /// The number of cells egglog's inline variant holds before it spills to the heap
        /// (<c>SmallVec&lt;[Cell&lt;Value&gt;; 64]&gt;</c>, mod.rs:67).
        /// </summary>
        public const int InlineCells = 64;

        readonly int arity;
        readonly int stride;
        readonly int firstCells;
        uint[] data;
        int count;

        /// <summary>
        /// Initializes an empty buffer for rows of <paramref name="arity"/> cells plus a tag. Allocates nothing until
        /// the first row. egglog's <c>TaggedRowBuffer::new</c>.
        /// </summary>
        /// <param name="arity">The number of cells per row, not counting the tag; may be zero.</param>
        public TaggedRowBuffer(int arity)
            : this(arity, 0)
        {

        }

        /// <summary>
        /// Initializes an empty buffer whose first allocation holds at least <paramref name="firstCells"/> cells.
        /// </summary>
        /// <param name="arity">The number of cells per row, not counting the tag; may be zero.</param>
        /// <param name="firstCells">The minimum size of the first allocation; zero for the ordinary growth
        /// policy.</param>
        TaggedRowBuffer(int arity, int firstCells)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(arity);
            ArgumentOutOfRangeException.ThrowIfEqual(arity, int.MaxValue);

            this.arity = arity;
            stride = arity + 1;
            this.firstCells = firstCells;
            data = Array.Empty<uint>();
        }

        /// <summary>
        /// Creates a buffer for short-lived scans with few rows, whose first allocation is the
        /// <see cref="InlineCells"/> cells egglog's <c>TaggedRowBuffer::new_inline</c> keeps on the stack. Like
        /// egglog's, it allocates no storage while it stays empty, which is the common case for free join's leaf scans
        /// (free_join/execute.rs:1394, 1586); .NET cannot put the cells on the stack inside a class, so the first row
        /// allocates them as one small, unpooled array. The buffer object itself is a heap allocation; a caller that
        /// needs none per scan keeps one buffer per scan site and <see cref="Clear"/>s it, which keeps its storage.
        /// </summary>
        /// <param name="arity">The number of cells per row, not counting the tag; may be zero.</param>
        /// <returns>The buffer.</returns>
        public static TaggedRowBuffer CreateInline(int arity)
        {
            return new TaggedRowBuffer(arity, InlineCells);
        }

        /// <summary>
        /// Gets the number of cells per row, not counting the tag. egglog's <c>base_arity</c>.
        /// </summary>
        public int Arity
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => arity;
        }

        /// <summary>
        /// Gets the number of physical cells per row: <see cref="Arity"/> + 1.
        /// </summary>
        public int Stride
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => stride;
        }

        /// <summary>
        /// Gets the number of rows, stale ones included.
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
        /// Gets the backing array, for code that indexes rows directly. Row <c>r</c> starts at <c>r * Stride</c>; its
        /// tag is at <c>r * Stride + Arity</c>. Replaced whenever the buffer grows or is disposed.
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
        /// Appends a row tagged with <paramref name="rowId"/> and returns its id in this buffer. Amortized
        /// O(<see cref="Arity"/>). egglog's <c>TaggedRowBuffer::add_row</c>.
        /// </summary>
        /// <param name="rowId">The id of the row in its source.</param>
        /// <param name="row">The row's cells; exactly <see cref="Arity"/> of them. May be a row of this buffer.</param>
        /// <returns>The new row's id in this buffer.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int AddRow(int rowId, ReadOnlySpan<uint> row)
        {
            if (row.Length != arity)
                ThrowArityMismatch(row.Length);

            var start = count * stride;
            if (data.Length - start < stride)
                return AddRowGrowing(rowId, row);

            ref var destination = ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(data), start);
            RowCells.Copy(ref MemoryMarshal.GetReference(row), ref destination, arity);
            Unsafe.Add(ref destination, arity) = (uint)rowId;
            return count++;
        }

        /// <summary>
        /// Appends the row whose cells start at <paramref name="source"/>[<paramref name="offset"/>], tagged with
        /// <paramref name="rowId"/>. The array-based form for compiled plans.
        /// </summary>
        /// <param name="rowId">The id of the row in its source.</param>
        /// <param name="source">An array holding the row's cells.</param>
        /// <param name="offset">The index of the row's first cell.</param>
        /// <returns>The new row's id in this buffer.</returns>
        public int AddRow(int rowId, uint[] source, int offset)
        {
            ArgumentNullException.ThrowIfNull(source);
            if (offset < 0 || source.Length - offset < arity)
                ThrowSourceOutOfRange();

            return AddRow(rowId, new ReadOnlySpan<uint>(source, offset, arity));
        }

        /// <inheritdoc />
        void IRowSink.AddRow(int rowId, ReadOnlySpan<uint> row)
        {
            AddRow(rowId, row);
        }

        /// <summary>
        /// Gets the cells of row <paramref name="row"/>, without the tag. O(1).
        /// </summary>
        /// <param name="row">A row id below <see cref="Count"/>.</param>
        /// <returns>The row's cells.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ReadOnlySpan<uint> GetRow(int row)
        {
            if ((uint)row >= (uint)count)
                ThrowRowOutOfRange(row);

            return MemoryMarshal.CreateReadOnlySpan(ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(data), row * stride), arity);
        }

        /// <summary>
        /// Gets the cells of row <paramref name="row"/> and the source row id it was tagged with. O(1). egglog's
        /// <c>get_row</c>.
        /// </summary>
        /// <param name="row">A row id below <see cref="Count"/>.</param>
        /// <param name="rowId">The source row id.</param>
        /// <returns>The row's cells.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ReadOnlySpan<uint> GetRow(int row, out int rowId)
        {
            if ((uint)row >= (uint)count)
                ThrowRowOutOfRange(row);

            ref var first = ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(data), row * stride);
            rowId = (int)Unsafe.Add(ref first, arity);
            return MemoryMarshal.CreateReadOnlySpan(ref first, arity);
        }

        /// <summary>
        /// Gets the cells of row <paramref name="row"/> for writing, and its source row id. O(1). egglog's
        /// <c>get_row_mut</c>.
        /// </summary>
        /// <param name="row">A row id below <see cref="Count"/>.</param>
        /// <param name="rowId">The source row id.</param>
        /// <returns>The row's cells.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public Span<uint> GetMutableRow(int row, out int rowId)
        {
            if ((uint)row >= (uint)count)
                ThrowRowOutOfRange(row);

            ref var first = ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(data), row * stride);
            rowId = (int)Unsafe.Add(ref first, arity);
            return MemoryMarshal.CreateSpan(ref first, arity);
        }

        /// <summary>
        /// Gets the source row id row <paramref name="row"/> was tagged with. O(1).
        /// </summary>
        /// <param name="row">A row id below <see cref="Count"/>.</param>
        /// <returns>The source row id.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int GetRowId(int row)
        {
            if ((uint)row >= (uint)count)
                ThrowRowOutOfRange(row);

            return (int)Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(data), row * stride + arity);
        }

        /// <summary>
        /// Gets the index in <see cref="Data"/> of the first cell of row <paramref name="row"/>, checking the row id.
        /// For compiled plans.
        /// </summary>
        /// <param name="row">A row id below <see cref="Count"/>.</param>
        /// <returns><c>row * Stride</c>.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int RowOffset(int row)
        {
            if ((uint)row >= (uint)count)
                ThrowRowOutOfRange(row);

            return row * stride;
        }

        /// <summary>
        /// Gets whether row <paramref name="row"/> is stale. O(1).
        /// </summary>
        /// <param name="row">A row id below <see cref="Count"/>.</param>
        /// <returns><c>true</c> if its first physical cell is <see cref="Value.StaleRep"/>.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool IsStale(int row)
        {
            if ((uint)row >= (uint)count)
                ThrowRowOutOfRange(row);

            return Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(data), row * stride) == Value.StaleRep;
        }

        /// <summary>
        /// Marks row <paramref name="row"/> stale. Idempotent. O(1). egglog's <c>TaggedRowBuffer::set_stale</c>.
        /// </summary>
        /// <param name="row">A row id below <see cref="Count"/>.</param>
        /// <returns>Whether the row was already stale.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool SetStale(int row)
        {
            if ((uint)row >= (uint)count)
                ThrowRowOutOfRange(row);

            ref var first = ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(data), row * stride);
            var was = first == Value.StaleRep;
            first = Value.StaleRep;
            return was;
        }

        /// <summary>
        /// Enumerates every row with its source row id, stale rows included, in insertion order. egglog's
        /// <c>iter</c>. The enumerator captures the storage and row count: rows added while it runs are not visited,
        /// and after a growth it reads (and writes) the old storage. Do not dispose the buffer while enumerating.
        /// </summary>
        /// <returns>The enumerator.</returns>
        public TaggedRowBufferEnumerator EnumerateRows()
        {
            return new TaggedRowBufferEnumerator(data, arity, count);
        }

        /// <summary>
        /// Enumerates the rows that are not stale with their source row ids, in insertion order. egglog's
        /// <c>non_stale</c> and <c>non_stale_mut</c>. Captures the storage and row count like
        /// <see cref="EnumerateRows"/>.
        /// </summary>
        /// <returns>The enumerator.</returns>
        public TaggedRowBufferNonStaleEnumerator EnumerateNonStale()
        {
            return new TaggedRowBufferNonStaleEnumerator(data, arity, count);
        }

        /// <summary>
        /// Returns the storage to the shared array pool and empties the buffer. The buffer stays usable; disposing
        /// twice is harmless. Spans, enumerators and <see cref="Data"/> references taken before must not be used
        /// afterwards: the pool may already have handed the storage to someone else.
        /// </summary>
        public void Dispose()
        {
            var old = data;
            data = Array.Empty<uint>();
            count = 0;
            PooledArray.Release(old);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        int AddRowGrowing(int rowId, ReadOnlySpan<uint> row)
        {
            var start = count * stride;
            var required = (long)start + stride;
            if (required > Array.MaxLength)
                throw new InvalidOperationException($"A tagged row buffer of arity {arity} cannot hold more than {Array.MaxLength / stride} rows.");

            // An inline buffer's first allocation is its 64 cells.
            if (data.Length == 0 && required < firstCells)
                required = firstCells;

            // The row may live in the old storage; that storage is left to the garbage collector rather than returned
            // to the pool (see PooledArray.Grow), so it stays intact for the copy and for anyone still reading it.
            var next = PooledArray.Grow(data, start, required);
            ref var destination = ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(next), start);
            RowCells.Copy(ref MemoryMarshal.GetReference(row), ref destination, arity);
            Unsafe.Add(ref destination, arity) = (uint)rowId;
            data = next;
            return count++;
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

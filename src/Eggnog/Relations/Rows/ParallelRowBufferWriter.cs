using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading;

namespace Eggnog.Relations
{

    /// <summary>
    /// Lets many threads append whole batches of rows to one row buffer at once, read rows back, and mark rows stale:
    /// egglog's <c>ParallelRowBufWriter</c> over <c>ParallelVecWriter</c>
    /// (core-relations/src/row_buffer/mod.rs:533-605, concurrency/src/parallel_writer.rs). The parallel merge of a
    /// table uses it: each shard's thread stages its output rows, appends them with one call, then points its hash
    /// entries at them.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Created by <see cref="RowBuffer.ParallelWriter"/>, which hands over the buffer's storage; <see cref="Finish"/>
    /// hands it back as a <see cref="RowBuffer"/> holding every appended row.
    /// </para>
    /// <para>
    /// An append reserves a disjoint range of cells with one atomic add on the reserved length, then copies the batch
    /// into it: one atomic and one block copy per batch, no per-row contention. When the caller reserved enough
    /// capacity up front (as egglog's merge does for every pending row) that is all. Otherwise the append that runs
    /// past the end grows the storage. Growth replaces the array, so it must not race with anyone still copying into
    /// or reading from the old one: appends and read handles run inside short read sections (a shared counter), and
    /// growth takes a lock, waits for the read sections in progress to drain, and holds new ones off until the new
    /// array is published. This is egglog's <c>ReadOptimizedLock</c> discipline: reads are cheap, the rare resize is
    /// exclusive.
    /// </para>
    /// <para>
    /// A thread holding a <see cref="ParallelRowBufferReadHandle"/> must not append (a resize would wait for its own
    /// read section forever), the same rule as in egglog, whose merge drops its read handle before staging more output.
    /// </para>
    /// </remarks>
    internal sealed class ParallelRowBufferWriter
    {

        readonly int arity;
        readonly object growLock = new();
        uint[] data;
        long reservedCells;
        int readers;
        int growing;
        bool finished;

        /// <summary>
        /// Initializes a writer that owns <paramref name="data"/>, whose first <paramref name="count"/> rows are
        /// already written.
        /// </summary>
        /// <param name="arity">The cells per row; at least one.</param>
        /// <param name="data">The storage, which the writer now owns.</param>
        /// <param name="count">The rows already written.</param>
        internal ParallelRowBufferWriter(int arity, uint[] data, int count)
        {
            Debug.Assert(arity >= 1);
            Debug.Assert(count >= 0 && (long)count * arity <= data.Length);

            this.arity = arity;
            this.data = data;
            reservedCells = (long)count * arity;
        }

        /// <summary>
        /// Gets the number of cells per row.
        /// </summary>
        public int Arity => arity;

        /// <summary>
        /// Gets the number of rows written or reserved so far. Exact once every append has returned.
        /// </summary>
        public int Count => (int)(Interlocked.Read(ref reservedCells) / arity);

        /// <summary>
        /// Appends every row of <paramref name="rows"/>, stale ones included, as one contiguous run, and returns the
        /// id of the first. Safe to call from many threads at once. egglog's <c>append_contents</c>.
        /// O(<paramref name="rows"/>.<see cref="RowBuffer.CellCount"/>) for the copy plus three atomic operations, and
        /// a resize when the reserved capacity runs out.
        /// </summary>
        /// <param name="rows">The rows; same arity as this writer. Not modified.</param>
        /// <returns>The id the first appended row has in the finished buffer.</returns>
        public int AppendContents(RowBuffer rows)
        {
            ArgumentNullException.ThrowIfNull(rows);
            if (rows.Arity != arity)
                throw new ArgumentException($"Expected rows of arity {arity} but got {rows.Arity}.", nameof(rows));
            if (finished)
                throw new InvalidOperationException("The writer has finished.");

            var cells = rows.CellCount;
            var start = Interlocked.Add(ref reservedCells, cells) - cells;
            Debug.Assert(start % arity == 0);
            if (cells == 0)
                return (int)(start / arity);

            var end = start + cells;
            if (Volatile.Read(ref data).Length < end)
                Grow(end);

            EnterRead();
            var target = Volatile.Read(ref data);
            Debug.Assert(target.Length >= end);
            rows.Data.AsSpan(0, cells).CopyTo(target.AsSpan((int)start, cells));
            ExitRead();

            return (int)(start / arity);
        }

        /// <summary>
        /// Opens a read section and returns a handle for reading rows and marking them stale while other threads keep
        /// appending. Dispose the handle to close the section (further disposes do nothing); keep it short, since a
        /// resize waits for it. egglog's <c>read_handle</c>.
        /// </summary>
        /// <returns>The handle.</returns>
        public ParallelRowBufferReadHandle ReadHandle()
        {
            if (finished)
                throw new InvalidOperationException("The writer has finished.");

            EnterRead();
            return new ParallelRowBufferReadHandle(this, Volatile.Read(ref data), arity);
        }

        /// <summary>
        /// Ends writing and returns a buffer holding the original rows followed by every appended run. Call it once,
        /// after every append has returned and every read handle has been disposed. egglog's <c>finish</c>.
        /// </summary>
        /// <returns>The buffer, which the caller owns.</returns>
        public RowBuffer Finish()
        {
            if (finished)
                throw new InvalidOperationException("The writer has already finished.");

            Debug.Assert(Volatile.Read(ref readers) == 0, "A read handle is still open.");
            finished = true;

            // Only an append that failed (the buffer would exceed the largest array) reserves past the storage.
            var cells = Interlocked.Read(ref reservedCells);
            if (cells > data.Length)
                throw new InvalidOperationException("An append failed; the writer's rows are incomplete.");

            var result = new RowBuffer(arity, data, (int)(cells / arity));
            data = Array.Empty<uint>();
            return result;
        }

        /// <summary>
        /// Opens a read section: while any is open, the storage is not replaced. Waits while a resize is in progress.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        void EnterRead()
        {
            // Announce the reader, then check for a resize. The interlocked increment is a full fence, and so is the
            // resizer's exchange on `growing` before it reads `readers`: one of the two always sees the other.
            Interlocked.Increment(ref readers);
            if (Volatile.Read(ref growing) != 0)
                EnterReadContended();
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        void EnterReadContended()
        {
            var spin = new SpinWait();
            while (true)
            {
                Interlocked.Decrement(ref readers);
                while (Volatile.Read(ref growing) != 0)
                    spin.SpinOnce();

                Interlocked.Increment(ref readers);
                if (Volatile.Read(ref growing) == 0)
                    return;
            }
        }

        /// <summary>
        /// Closes a read section opened by <see cref="EnterRead"/> or by <see cref="ReadHandle"/>. Each section is
        /// closed exactly once: <see cref="AppendContents"/> pairs its own, and a handle's
        /// <see cref="ParallelRowBufferReadHandle.Dispose"/> runs this at most once.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal void ExitRead()
        {
            var remaining = Interlocked.Decrement(ref readers);
            Debug.Assert(remaining >= 0, "A read section was closed twice.");
        }

        /// <summary>
        /// Replaces the storage with one of at least <paramref name="requiredCells"/> cells, at least doubling it.
        /// Excludes every read section while it copies.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        void Grow(long requiredCells)
        {
            if (requiredCells > Array.MaxLength)
                throw new InvalidOperationException($"A row buffer of arity {arity} cannot hold more than {Array.MaxLength / arity} rows.");

            lock (growLock)
            {
                var current = data;
                if (current.Length >= requiredCells)
                    return;

                // Hold off new read sections, then wait for the ones in progress to finish their copies and reads.
                Interlocked.Exchange(ref growing, 1);
                var spin = new SpinWait();
                while (Volatile.Read(ref readers) != 0)
                    spin.SpinOnce();

                // Ranges past the old length were reserved but cannot have been written yet: their writers are waiting
                // to enter a read section and will copy into the new array.
                var used = (int)Math.Min(Interlocked.Read(ref reservedCells), current.Length);
                var next = PooledArray.Grow(current, used, requiredCells);
                Volatile.Write(ref data, next);
                Volatile.Write(ref growing, 0);

                // The old array goes to the garbage collector, not back to the pool, like every growth in the row
                // buffers (and like Vec::reserve in egglog's ParallelVecWriter): anyone who still holds it, such as an
                // enumerator over the buffer this writer came from, never sees it recycled.
            }
        }

    }

}

using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;

namespace Eggnog.Relations
{

    /// <summary>
    /// An open read section on a <see cref="ParallelRowBufferWriter"/>: reads rows and marks rows stale while other
    /// threads append. egglog's <c>ReadHandle</c> (core-relations/src/row_buffer/mod.rs:567-605). While any handle is
    /// open the writer's storage is not replaced, so the handle reads the array it captured.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A row may be read when it was in the buffer before writing began or was appended by a write that completed
    /// before the read (on this thread, or published to it by other synchronization), as in egglog.
    /// </para>
    /// <para>
    /// The handle owns one count in the writer's read-section counter, and <see cref="Dispose"/> gives it back exactly
    /// once however often, and from however many threads, it is called; egglog's handle is a guard that cannot be
    /// duplicated. It is a class rather than a struct so that copies of it are the same handle. One is created per
    /// batch of reads (in the parallel merge, per shard flush), so the allocation does not matter. Do not use a handle
    /// after disposing it, and do not append on a thread that holds an open one: a resize would wait for it forever,
    /// the same rule as in egglog.
    /// </para>
    /// </remarks>
    internal sealed class ParallelRowBufferReadHandle : IDisposable
    {

        readonly uint[] data;
        readonly int arity;
        ParallelRowBufferWriter? writer;

        /// <summary>
        /// Initializes a handle over a read section the caller has just opened on <paramref name="writer"/>.
        /// </summary>
        /// <param name="writer">The writer whose read section this is.</param>
        /// <param name="data">The writer's storage, stable while the section is open.</param>
        /// <param name="arity">The cells per row.</param>
        internal ParallelRowBufferReadHandle(ParallelRowBufferWriter writer, uint[] data, int arity)
        {
            this.writer = writer;
            this.data = data;
            this.arity = arity;
        }

        /// <summary>
        /// Gets the storage, for direct indexing: row <c>r</c> starts at <c>r * Arity</c>. Valid until the handle is
        /// disposed.
        /// </summary>
        public uint[] Data => data;

        /// <summary>
        /// Gets the cells per row.
        /// </summary>
        public int Arity => arity;

        /// <summary>
        /// Gets the cells of row <paramref name="row"/>. Checked against the storage, not against completed writes.
        /// </summary>
        /// <param name="row">A row that may be read (see the remarks on the type).</param>
        /// <returns>The row's cells.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ReadOnlySpan<uint> GetRow(int row)
        {
            AssertOpen();
            return new ReadOnlySpan<uint>(data, row * arity, arity);
        }

        /// <summary>
        /// Gets the cells of row <paramref name="row"/> without a bounds check. egglog's <c>get_row_unchecked</c>; the
        /// caller guarantees the row lies within the storage.
        /// </summary>
        /// <param name="row">A row that may be read.</param>
        /// <returns>The row's cells.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ReadOnlySpan<uint> GetRowUnchecked(int row)
        {
            AssertOpen();
            Debug.Assert(row >= 0 && ((long)row + 1) * arity <= data.Length);
            return MemoryMarshal.CreateReadOnlySpan(ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(data), row * arity), arity);
        }

        /// <summary>
        /// Gets whether row <paramref name="row"/> is stale.
        /// </summary>
        /// <param name="row">A row that may be read.</param>
        /// <returns><c>true</c> if its first cell is <see cref="Value.StaleRep"/>.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool IsStale(int row)
        {
            AssertOpen();
            return data[row * arity] == Value.StaleRep;
        }

        /// <summary>
        /// Marks row <paramref name="row"/> stale. The caller guarantees no other thread reads or writes that row at
        /// the same time; in the parallel merge, each row belongs to the one shard whose thread touches it. egglog's
        /// <c>ReadHandle::set_stale_shared</c>.
        /// </summary>
        /// <param name="row">A row that may be read.</param>
        /// <returns>Whether the row was already stale.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool SetStaleShared(int row)
        {
            AssertOpen();
            ref var first = ref data[row * arity];
            var was = first == Value.StaleRep;
            first = Value.StaleRep;
            return was;
        }

        /// <summary>
        /// Closes the read section. Only the first call does anything, so a handle disposed twice (or through two
        /// references) still closes its section once.
        /// </summary>
        public void Dispose()
        {
            Interlocked.Exchange(ref writer, null)?.ExitRead();
        }

        [Conditional("DEBUG")]
        void AssertOpen()
        {
            Debug.Assert(Volatile.Read(ref writer) is not null, "A read handle was used after it was disposed.");
        }

    }

}

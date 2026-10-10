using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Eggnog.Relations
{

    /// <summary>
    /// Read access to the vector behind a <see cref="ParallelVecWriter{T}"/>, held until disposed. While it is held the
    /// storage cannot be reallocated, so the buffer seen through it stays the live one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Covers both of the Rust's handles (parallel_writer.rs:22-93): <see cref="Prefix"/> is <c>read_access</c>, the
    /// elements present when the writer was created, and <see cref="Get"/>, <see cref="GetSlice"/> and
    /// <see cref="Buffer"/> are <c>UnsafeReadAccess</c>, which may also reach ranges written later. In C# such
    /// accesses are bounds-checked against the storage, so a mistake reads a default value instead of undefined
    /// memory; it is still the caller's job to read only the prefix or ranges whose write has completed.
    /// </para>
    /// <para>
    /// <see cref="Buffer"/> also allows in-place writes to elements the caller owns, which is what core-relations'
    /// <c>set_stale_shared</c> does through <c>Cell</c>s (row_buffer/mod.rs:597-604). Such a write is never lost to a
    /// concurrent reallocation, because reallocation waits for every handle to be disposed.
    /// </para>
    /// <para>
    /// Dispose exactly once; holding the handle blocks writers that need to grow the storage, and taking one while
    /// already holding one on the same thread can deadlock with such a writer.
    /// </para>
    /// </remarks>
    /// <typeparam name="T">The element type.</typeparam>
    internal readonly struct ParallelVecReadAccess<T> : IDisposable
    {

        readonly ReadOptimizedLockReader<T[]> reader;
        readonly int prefixLength;

        /// <summary>
        /// Initializes a new instance over a read lock on the writer's storage.
        /// </summary>
        /// <param name="reader">The read lock.</param>
        /// <param name="prefixLength">The length of the vector when the writer was created.</param>
        internal ParallelVecReadAccess(ReadOptimizedLockReader<T[]> reader, int prefixLength)
        {
            this.reader = reader;
            this.prefixLength = prefixLength;
        }

        /// <summary>
        /// Gets the elements present when the writer was created.
        /// </summary>
        public ReadOnlySpan<T> Prefix => new(reader.Value, 0, prefixLength);

        /// <summary>
        /// Gets the number of elements present when the writer was created.
        /// </summary>
        public int PrefixLength => prefixLength;

        /// <summary>
        /// Gets the whole storage: the prefix, completed and in-flight writes, and unused capacity.
        /// </summary>
        public T[] Buffer
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => reader.Value;
        }

        /// <summary>
        /// Gets the element at <paramref name="index"/>, which must lie in the prefix or in a completed write. The
        /// Rust's <c>get_unchecked</c>; here one explicit check against the storage keeps it memory-safe.
        /// </summary>
        /// <param name="index">The index.</param>
        /// <returns>A reference to the element.</returns>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="index"/> is outside the storage.</exception>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ref readonly T Get(int index)
        {
            var buffer = reader.Value;
            if ((uint)index >= (uint)buffer.Length)
                ThrowIndexOutOfRange(index, buffer.Length);

            // Checked just above.
            return ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(buffer), index);
        }

        /// <summary>
        /// Gets <paramref name="length"/> elements starting at <paramref name="start"/>, which must lie in the prefix
        /// or in completed writes. The Rust's <c>get_unchecked_slice</c>; here one explicit check against the storage
        /// keeps it memory-safe.
        /// </summary>
        /// <param name="start">The first index.</param>
        /// <param name="length">The number of elements.</param>
        /// <returns>The elements.</returns>
        /// <exception cref="ArgumentOutOfRangeException">The range is not within the storage.</exception>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ReadOnlySpan<T> GetSlice(int start, int length)
        {
            var buffer = reader.Value;

            // Negative values become huge as unsigned, so this one test rejects them too.
            if ((ulong)(uint)start + (uint)length > (uint)buffer.Length)
                ThrowRangeOutOfRange(start, length, buffer.Length);

            // Checked just above.
            return MemoryMarshal.CreateReadOnlySpan(ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(buffer), start), length);
        }

        /// <summary>
        /// Releases the read access.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Dispose() => reader.Dispose();

        /// <summary>
        /// Throws for an index outside the storage.
        /// </summary>
        /// <param name="index">The index.</param>
        /// <param name="storageLength">The storage length.</param>
        [MethodImpl(MethodImplOptions.NoInlining)]
        static void ThrowIndexOutOfRange(int index, int storageLength)
        {
            throw new ArgumentOutOfRangeException(nameof(index), index, $"Index {index} is outside the storage of length {storageLength}.");
        }

        /// <summary>
        /// Throws for a range outside the storage.
        /// </summary>
        /// <param name="start">The first index.</param>
        /// <param name="length">The number of elements.</param>
        /// <param name="storageLength">The storage length.</param>
        [MethodImpl(MethodImplOptions.NoInlining)]
        static void ThrowRangeOutOfRange(int start, int length, int storageLength)
        {
            throw new ArgumentOutOfRangeException(nameof(start), start, $"The range of {length} elements at {start} is outside the storage of length {storageLength}.");
        }

    }

}

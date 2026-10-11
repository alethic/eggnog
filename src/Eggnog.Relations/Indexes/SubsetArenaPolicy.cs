using System;
using System.Runtime.CompilerServices;

namespace Eggnog.Relations
{

    /// <summary>
    /// Pool policy for the arena array of a <see cref="SubsetBuffer"/>: egglog's <c>rows</c> pool of
    /// <c>Vec&lt;RowId&gt;</c> (core-relations/src/pool/mod.rs:48-58, 424-446; 32 MiB per thread), which every
    /// <c>SubsetBuffer</c> takes its <c>buf</c> from (core-relations/src/hash_index/mod.rs:802-822).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The pool remembers whole arrays, so an arena rented again starts with the length it last grew to, as a pooled
    /// <c>Vec</c> keeps its capacity: an index rebuilt over a similar subset appends without regrowing.
    /// <see cref="System.Buffers.ArrayPool{T}"/> rents by requested length, which an arena does not know when it
    /// starts, so it cannot give that.
    /// </para>
    /// <para>
    /// An array is kept only when it is longer than <see cref="PooledArray.MaxUnpooledLength"/> (egglog keeps a
    /// <c>Vec</c> only above 256 elements), and weighs its length times four bytes. Clearing does nothing: arena
    /// contents are unspecified until a block is written, as a cleared <c>Vec</c>'s spare capacity is.
    /// <see cref="Create"/> hands out the shared empty array, which is never admitted back.
    /// </para>
    /// </remarks>
    internal readonly struct SubsetArenaPolicy : IPoolPolicy<int[]>
    {

        /// <inheritdoc />
        public static int[] Create() => Array.Empty<int>();

        /// <inheritdoc />
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Clear(int[] item)
        {

        }

        /// <inheritdoc />
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool ShouldReuse(int[] item) => item.Length > PooledArray.MaxUnpooledLength;

        /// <inheritdoc />
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static long Bytes(int[] item) => (long)item.Length * sizeof(int);

        /// <inheritdoc />
        public static long LimitBytes => PoolLimits.Rows;

    }

}

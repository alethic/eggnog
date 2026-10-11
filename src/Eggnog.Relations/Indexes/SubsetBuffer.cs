using System;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace Eggnog.Relations
{

    /// <summary>
    /// The arena holding the sparse row lists of one index shard, with a free list per power-of-two size class:
    /// egglog's <c>SubsetBuffer</c> and <c>FreeList</c> (core-relations/src/hash_index/mod.rs:793-902, 994-1013).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Layout: one <see cref="int"/> array. A run of <c>L &gt;= 1</c> row ids occupies a block of
    /// <c>next_pow2(L)</c> slots starting at its first position, so a run whose length is not a power of two has room
    /// to append in place, and a full one moves to a block of the next size class. Blocks are never split or
    /// coalesced. Freed blocks go on the free list of their size class (class <c>c</c> holds blocks of <c>2^c</c>
    /// slots, 32 classes) and are reused last-in first-out, as egglog pops its per-class <c>Vec</c>.
    /// </para>
    /// <para>
    /// The free lists are intrusive: a free block stores the start of the next free block of its class in its first
    /// slot, so the whole free-list state is 32 heads and costs no allocation per free block. egglog keeps a
    /// <c>Vec&lt;BufferIndex&gt;</c> per class; the order of reuse is the same.
    /// </para>
    /// <para>
    /// The point of the arena (egglog's comment, mod.rs:793-801) is that dropping or clearing an index costs O(shards)
    /// rather than a walk over every key: <see cref="Reset"/> forgets every block at once.
    /// </para>
    /// <para>
    /// Storage: egglog's <c>buf</c> is a <c>Pooled&lt;Vec&lt;RowId&gt;&gt;</c> from its <c>rows</c> pool. Here the first
    /// block of an empty arena takes the most recently released arena array of the calling thread from the pool of
    /// <see cref="SubsetArenaPolicy"/>, at the length it last grew to, and <see cref="Release"/> hands the array back,
    /// so an index rebuilt over a similar subset does not regrow its arena. Growth beyond that allocates a new array
    /// (at least doubling, like <c>Vec</c> growth) and drops the old one to the garbage collector, so a
    /// <see cref="SubsetRef"/> taken before a growth keeps reading consistent (old) contents. Arena arrays live only in
    /// that pool: they are never rented from or returned to <see cref="System.Buffers.ArrayPool{T}.Shared"/>, which
    /// would otherwise lose every array an index takes from it. Debug builds fill fresh blocks with -1 so that reading
    /// a slot never written is caught.
    /// </para>
    /// <para>
    /// A mutable struct embedded in its shard: mutate it only through a field or array element, never through a copy.
    /// Not thread-safe; each shard's arena has one writer at a time.
    /// </para>
    /// </remarks>
    internal struct SubsetBuffer
    {

        /// <summary>
        /// The number of size classes: blocks of <c>2^0</c> to <c>2^31</c> slots, egglog's <c>[Vec&lt;BufferIndex&gt;; 32]</c>.
        /// </summary>
        internal const int SizeClassCount = 32;

        /// <summary>
        /// The value debug builds write into fresh arena slots.
        /// </summary>
        internal const int UnwrittenSlot = -1;

        int[] rows;
        int length;
        int[]? freeHeads;

        /// <summary>
        /// Creates an empty arena that rents and allocates nothing until the first block.
        /// </summary>
        /// <returns>The arena.</returns>
        public static SubsetBuffer Create()
        {
            var buffer = default(SubsetBuffer);
            buffer.rows = Array.Empty<int>();
            return buffer;
        }

        /// <summary>
        /// Gets the arena array that sparse <see cref="BufferedSubset"/>s index. Replaced when the arena grows.
        /// </summary>
        public readonly int[] Rows
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => rows;
        }

        /// <summary>
        /// Gets the number of arena slots handed out as blocks so far (egglog's <c>buf.len()</c>), free blocks included.
        /// </summary>
        public readonly int Length => length;

        /// <summary>
        /// Gets the size class of a run of <paramref name="count"/> row ids: <c>log2(next_pow2(count))</c>, with zero for
        /// counts below two (egglog's <c>FreeList::get_size_class</c>).
        /// </summary>
        /// <param name="count">The run length.</param>
        /// <returns>The class.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int SizeClass(int count) => count <= 1 ? 0 : 32 - BitOperations.LeadingZeroCount((uint)count - 1);

        /// <summary>
        /// Gets the number of slots a run of <paramref name="count"/> row ids occupies: <c>next_pow2(count)</c>, at least one.
        /// </summary>
        /// <param name="count">The run length.</param>
        /// <returns>The block size.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static long BlockSize(int count) => 1L << SizeClass(count);

        /// <summary>
        /// Allocates a block for a run of <paramref name="count"/> row ids, from the free list of its class when it has
        /// one, otherwise at the end of the arena. egglog's <c>new_vec</c> without the fill. Amortized O(1); the caller
        /// writes the run into <c>Rows[start .. start + count)</c>, re-reading <see cref="Rows"/> afterwards.
        /// </summary>
        /// <param name="count">The run length; at least one.</param>
        /// <returns>The block's first position.</returns>
        public int Allocate(int count)
        {
            Debug.Assert(count >= 1);
            var sizeClass = SizeClass(count);
            var heads = freeHeads;
            if (heads is not null)
            {
                var head = heads[sizeClass];
                if (head >= 0)
                {
                    heads[sizeClass] = rows[head];
                    return head;
                }
            }

            var start = length;
            var end = start + (1L << sizeClass);
            if (end > rows.Length)
                Grow(end);

            length = (int)end;
            FillUnwritten(start, (int)end);
            return start;
        }

        /// <summary>
        /// Copies <paramref name="run"/> (sorted) into a new block. egglog's <c>new_vec</c>. O(run length).
        /// </summary>
        /// <param name="run">The row ids; at least one.</param>
        /// <returns>The sparse subset.</returns>
        public BufferedSubset NewRun(ReadOnlySpan<int> run)
        {
            var start = Allocate(run.Length);
            run.CopyTo(rows.AsSpan(start));
            return BufferedSubset.Sparse(start, start + run.Length);
        }

        /// <summary>
        /// Moves the dense range <c>[first, end)</c> into the arena and appends <paramref name="row"/>: the
        /// dense-to-sparse arm of egglog's <c>add_row_sorted</c> (core-relations/src/hash_index/mod.rs:951-953).
        /// egglog fills a block for the range and then pushes, which moves a power-of-two range into a second block at
        /// once; this allocates the block for <c>end - first + 1</c> rows directly, with the same result. O(range).
        /// </summary>
        /// <param name="first">The range's first row id.</param>
        /// <param name="end">One past the range's last row id; greater than <paramref name="first"/>.</param>
        /// <param name="row">The row to append; at least <c>end - 1</c>.</param>
        /// <returns>The sparse subset.</returns>
        [MethodImpl(MethodImplOptions.NoInlining)]
        public BufferedSubset PromoteDense(int first, int end, int row)
        {
            Debug.Assert(first < end && row >= end - 1, $"row {row} appended to [{first}, {end})");
            var count = end - first;
            var start = Allocate(count + 1);
            var span = rows.AsSpan(start, count + 1);
            for (var i = 0; i < count; i++)
                span[i] = first + i;

            span[count] = row;
            return BufferedSubset.Sparse(start, start + count + 1);
        }

        /// <summary>
        /// Appends <paramref name="row"/> to the run at <c>[start, end)</c>. egglog's <c>push_vec</c>
        /// (core-relations/src/hash_index/mod.rs:856-885): when the run's length is not a power of two its block has room
        /// and the row is written in place; otherwise the run moves to a block of the next size class (reused from the
        /// free list or appended) and its old block is freed. Amortized O(1).
        /// </summary>
        /// <param name="start">The run's first position.</param>
        /// <param name="end">One past the run's last position; greater than <paramref name="start"/>.</param>
        /// <param name="row">The row to append; at least the run's last row.</param>
        /// <returns>The run's new location.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public BufferedSubset Push(int start, int end, int row)
        {
            var count = end - start;
            Debug.Assert(count >= 1 && rows[end - 1] <= row, $"row {row} appended after {rows[end - 1]}");
            if (BitOperations.IsPow2(count) == false)
            {
                rows[end] = row;
                return BufferedSubset.Sparse(start, end + 1);
            }

            return Move(start, count, row);
        }

        /// <summary>
        /// Frees the block of the run at <c>[start, start + count)</c>. egglog's <c>return_vec</c>. O(1).
        /// </summary>
        /// <param name="start">The run's first position.</param>
        /// <param name="count">The run's length; at least one.</param>
        public void Free(int start, int count)
        {
            Debug.Assert(count >= 1 && start >= 0 && start + BlockSize(count) <= length);
            var heads = freeHeads ??= CreateHeads();
            var sizeClass = SizeClass(count);
            rows[start] = heads[sizeClass];
            heads[sizeClass] = start;
        }

        /// <summary>
        /// Makes room for <paramref name="slots"/> more slots at the end of the arena, so that blocks of that total size
        /// allocate without growing. For bulk builds that know their arena size.
        /// </summary>
        /// <param name="slots">The number of slots.</param>
        public void Reserve(long slots)
        {
            var end = length + slots;
            if (end > rows.Length)
                Grow(end);
        }

        /// <summary>
        /// Forgets every block, keeping the storage: what clearing an index does to its arena. egglog returns each sparse
        /// run to the free list one by one while it drains the map (core-relations/src/hash_index/mod.rs:215-226); since
        /// every block is free afterwards, truncating the arena is equivalent and O(1). The free-list heads are emptied in
        /// place, so an index cleared and refilled allocates no new heads.
        /// </summary>
        public void Reset()
        {
            length = 0;
            freeHeads?.AsSpan().Fill(-1);
        }

        /// <summary>
        /// Creates an independent copy holding the same blocks and free lists, in a new array of exactly
        /// <see cref="Length"/> slots. O(<see cref="Length"/>).
        /// </summary>
        /// <returns>The copy.</returns>
        public readonly SubsetBuffer Clone()
        {
            var copy = default(SubsetBuffer);
            copy.rows = length == 0 ? Array.Empty<int>() : GC.AllocateUninitializedArray<int>(length);
            rows.AsSpan(0, length).CopyTo(copy.rows);
            copy.length = length;
            copy.freeHeads = (int[]?)freeHeads?.Clone();
            return copy;
        }

        /// <summary>
        /// Hands the storage back to the pool of <see cref="SubsetArenaPolicy"/> (egglog's drop of the pooled
        /// <c>buf</c>) and empties the arena, which stays usable. Subsets taken from it must not be used afterwards.
        /// </summary>
        /// <remarks>
        /// Two things stay with the arena, so that an index disposed and rebuilt in place
        /// (<see cref="ColumnIndex.RebuildForSubset"/>, the join's recycled trie nodes) allocates nothing: the free-list
        /// heads, emptied in place, and an array too short for the pool to keep (at most
        /// <see cref="PooledArray.MaxUnpooledLength"/> slots), which the pool would drop and the next build would
        /// allocate again. The next growth of the empty arena still tries the pool first (<see cref="Grow"/>). What
        /// stays is bounded by about a kilobyte per arena.
        /// </remarks>
        public void Release()
        {
            var old = rows;
            length = 0;
            freeHeads?.AsSpan().Fill(-1);
            if (old is null || SubsetArenaPolicy.ShouldReuse(old) == false)
                return;

            rows = Array.Empty<int>();
            SharedPool<int[], SubsetArenaPolicy>.Return(old);
        }

        /// <summary>
        /// Gets whether no slot in <c>[start, end)</c> of <paramref name="arena"/> still holds the debug fill value.
        /// O(end - start); for debug assertions only (egglog's <c>make_ref</c> check, mod.rs:895-899).
        /// </summary>
        /// <param name="arena">The arena array.</param>
        /// <param name="start">The first position.</param>
        /// <param name="end">One past the last position.</param>
        /// <returns><c>true</c> if every slot was written.</returns>
        internal static bool IsFilled(int[] arena, int start, int end) => arena.AsSpan(start, end - start).IndexOf(UnwrittenSlot) < 0;

        /// <summary>
        /// The full-block arm of <see cref="Push"/>.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        BufferedSubset Move(int start, int count, int row)
        {
            var next = Allocate(count + 1);
            var data = rows;
            Array.Copy(data, start, data, next, count);
            data[next + count] = row;
            Free(start, count);
            return BufferedSubset.Sparse(next, next + count + 1);
        }

        /// <summary>
        /// Grows the storage to at least <paramref name="required"/> slots. An empty arena without storage, or with only
        /// an array too short for the pool (one <see cref="Release"/> kept), first takes a recycled array from the pool
        /// of <see cref="SubsetArenaPolicy"/>, as egglog's <c>SubsetBuffer::default</c> takes a pooled vector; only if
        /// that is too short does it grow, from the recycled length. Growth follows
        /// <see cref="PooledArray.Grow{T}"/>'s amortization (at least double, at least
        /// <see cref="PooledArray.MinGrowLength"/> slots) but allocates directly, since the array will go back to the
        /// arena pool rather than the shared array pool.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        void Grow(long required)
        {
            if (required > Array.MaxLength)
                throw new InvalidOperationException($"An index shard cannot hold more than {Array.MaxLength} row ids.");

            if (length == 0 && SubsetArenaPolicy.ShouldReuse(rows) == false)
            {
                // Every pooled array is longer than any array the pool refuses, so a recycled one replaces the short
                // array (dropped, as the pool would have dropped it) unless the pool is empty.
                var recycled = SharedPool<int[], SubsetArenaPolicy>.Rent();
                Debug.Assert(recycled.Length == 0 || recycled.Length > rows.Length);
                if (recycled.Length > rows.Length)
                    rows = recycled;

                if (required <= rows.Length)
                    return;
            }

            var size = Math.Max((long)rows.Length * 2, Math.Max(required, PooledArray.MinGrowLength));
            if (size > Array.MaxLength)
                size = Array.MaxLength;

            var next = GC.AllocateUninitializedArray<int>((int)size);
            rows.AsSpan(0, length).CopyTo(next);
            rows = next;
        }

        /// <summary>
        /// Creates the free-list heads, every class empty.
        /// </summary>
        static int[] CreateHeads()
        {
            var heads = new int[SizeClassCount];
            heads.AsSpan().Fill(-1);
            return heads;
        }

        /// <summary>
        /// Debug builds: marks the slots of a fresh block as unwritten.
        /// </summary>
        [Conditional("DEBUG")]
        readonly void FillUnwritten(int start, int end)
        {
            rows.AsSpan(start, end - start).Fill(UnwrittenSlot);
        }

    }

}

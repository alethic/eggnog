namespace Eggnog.Relations
{

    /// <summary>
    /// One block of ids an <see cref="IdBlockAllocator"/> reserved on the store's id counter: the ids
    /// <c>[Start, End)</c>, handed out in order by interlocked increments of <see cref="Next"/>.
    /// </summary>
    /// <remarks>
    /// <see cref="Next"/> may run past <see cref="End"/> when several threads race at the end of the block; values at or
    /// past <see cref="End"/> are never handed out, and the allocator then reserves a new block. The block is
    /// immutable apart from <see cref="Next"/>.
    /// </remarks>
    internal sealed class IdBlock
    {

        /// <summary>
        /// The next id to hand out; advanced with <see cref="System.Threading.Interlocked.Increment(ref long)"/>.
        /// </summary>
        public long Next;

        /// <summary>
        /// The first id of the block.
        /// </summary>
        public readonly long Start;

        /// <summary>
        /// One past the last id of the block.
        /// </summary>
        public readonly long End;

        /// <summary>
        /// The number of ids reserved by the allocator's earlier blocks, so that the allocator's minted count can be
        /// read from the current block alone.
        /// </summary>
        public readonly long ReservedBefore;

        /// <summary>
        /// Initializes the block <c>[<paramref name="start"/>, <paramref name="end"/>)</c>.
        /// </summary>
        /// <param name="start">The first id.</param>
        /// <param name="end">One past the last id; at least <paramref name="start"/>.</param>
        /// <param name="reservedBefore">The ids reserved by earlier blocks of the same allocator.</param>
        public IdBlock(long start, long end, long reservedBefore)
        {
            Next = start;
            Start = start;
            End = end;
            ReservedBefore = reservedBefore;
        }

    }

}

using System;
using System.Runtime.CompilerServices;

namespace Eggnog.Relations
{

    /// <summary>
    /// The hits of one probe of a prober with several pieces: for each piece whose layer holds the key, the rows there.
    /// The probed side of a layered join is the union of these, one row set per layer, which the stage refines for
    /// visibility and constraints into a <see cref="LayeredSubset"/>.
    /// </summary>
    /// <remarks>
    /// Per-position scratch of a join task: filled by one probe and read by that probe's refinement before the position
    /// probes again. The rows are borrowed from the pieces' indexes, which live as long as the stage. Allocation-free once
    /// grown to the run's layer count. Not thread-safe.
    /// </remarks>
    internal sealed class LayerHits
    {

        /// <summary>
        /// The rows of each hit; the first <see cref="Count"/> are valid.
        /// </summary>
        public SubsetRef[] Rows;

        /// <summary>
        /// The prober piece of each hit; the first <see cref="Count"/> are valid.
        /// </summary>
        public int[] Pieces;

        /// <summary>
        /// The number of hits: pieces whose layer holds the key.
        /// </summary>
        public int Count;

        /// <summary>
        /// The number of rows over every hit.
        /// </summary>
        public int Total;

        /// <summary>
        /// Initializes empty scratch for probers of up to <paramref name="capacity"/> pieces.
        /// </summary>
        /// <param name="capacity">The expected piece count; the arrays grow beyond it.</param>
        public LayerHits(int capacity)
        {
            capacity = Math.Max(2, capacity);
            Rows = new SubsetRef[capacity];
            Pieces = new int[capacity];
        }

        /// <summary>
        /// Forgets every hit.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Clear()
        {
            Count = 0;
            Total = 0;
        }

        /// <summary>
        /// Records that piece <paramref name="piece"/> holds the key in <paramref name="rows"/>.
        /// </summary>
        /// <param name="piece">The piece's index in its prober.</param>
        /// <param name="rows">The rows, borrowed from the piece's index.</param>
        public void Add(int piece, SubsetRef rows)
        {
            if (Count == Rows.Length)
            {
                Array.Resize(ref Rows, Count * 2);
                Array.Resize(ref Pieces, Count * 2);
            }

            Rows[Count] = rows;
            Pieces[Count] = piece;
            Count++;
            Total += rows.Size;
        }

    }

}

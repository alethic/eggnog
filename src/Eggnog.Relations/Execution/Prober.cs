using System;
using System.Runtime.CompilerServices;

namespace Eggnog.Relations
{

    /// <summary>
    /// The indexes over some columns of an atom's current rows, one per layer of those rows, together with the atom's
    /// trie node, which the stage holds while it runs: egglog's <c>Prober</c>
    /// (core-relations/src/free_join/execute.rs:155-345), split into <see cref="ProberPiece"/>s (colored-contexts spec
    /// §9.5).
    /// </summary>
    /// <remarks>
    /// <para>
    /// A root prober has one piece, <see cref="First"/>, built with egglog's heuristics; every probe then takes today's
    /// path after one <see cref="PieceCount"/> compare. A child's atom whose rows lie in several layers gets one piece per
    /// such layer, the first in <see cref="First"/> and the others in <see cref="Rest"/>; a driver enumerates the pieces
    /// one after the other (one frame per (piece, key)), and a probe of several pieces joins its hits across layers.
    /// </para>
    /// <para>
    /// A plain struct kept in per-position scratch arrays. <see cref="Rest"/> is kept between uses of the scratch slot
    /// (only <see cref="RestCount"/> is reset), so a warm layered run allocates no piece array per stage.
    /// </para>
    /// </remarks>
    internal struct Prober
    {

        /// <summary>
        /// The atom's trie node, moved out of the bindings for the stage's duration.
        /// </summary>
        public TrieNode Node;

        /// <summary>
        /// The first piece: the only one of a single-layer atom.
        /// </summary>
        public ProberPiece First;

        /// <summary>
        /// The other pieces, in ascending layer order; the first <see cref="RestCount"/> are in use.
        /// </summary>
        public ProberPiece[]? Rest;

        /// <summary>
        /// The number of pieces in <see cref="Rest"/> in use.
        /// </summary>
        public int RestCount;

        /// <summary>
        /// Gets the number of pieces: one per layer of the atom's rows.
        /// </summary>
        public readonly int PieceCount
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => RestCount + 1;
        }

        /// <summary>
        /// Gets the number of distinct keys over every piece, counting a key once per piece holding it: what picks the
        /// side an intersection iterates (egglog's <c>len</c>).
        /// </summary>
        public readonly int Count
        {
            get
            {
                var count = First.Count;
                for (var i = 0; i < RestCount; i++)
                    count += Rest![i].Count;

                return count;
            }
        }

        /// <summary>
        /// Gets piece <paramref name="index"/> of <paramref name="prober"/> by reference.
        /// </summary>
        /// <param name="prober">The prober.</param>
        /// <param name="index">The piece, in <c>[0, <see cref="PieceCount"/>)</c>.</param>
        /// <returns>The piece.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ref ProberPiece GetPiece(ref Prober prober, int index)
        {
            if (index == 0)
                return ref prober.First;

            return ref prober.Rest![index - 1];
        }

        /// <summary>
        /// Gets piece <paramref name="index"/>, read-only.
        /// </summary>
        /// <param name="index">The piece, in <c>[0, <see cref="PieceCount"/>)</c>.</param>
        /// <returns>The piece.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public readonly ProberPiece Piece(int index) => index == 0 ? First : Rest![index - 1];

        /// <summary>
        /// Adds a piece after the ones in use, growing <see cref="Rest"/> when needed (once per scratch slot and layer
        /// count), and returns it for the caller to fill.
        /// </summary>
        /// <returns>The new piece, cleared.</returns>
        public ref ProberPiece AddPiece()
        {
            if (Rest is null || Rest.Length == RestCount)
                Array.Resize(ref Rest, Math.Max(4, RestCount * 2));

            ref var piece = ref Rest[RestCount++];
            piece = default;
            return ref piece;
        }

        /// <summary>
        /// Looks up the rows holding <paramref name="key"/> in a one-column prober of one piece: egglog's
        /// <c>get_subset</c>.
        /// </summary>
        /// <param name="key">The value.</param>
        /// <param name="rows">The rows, nonempty and sorted; borrowed from the index.</param>
        /// <returns><c>true</c> if any of the atom's rows holds the value.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public readonly bool TryGetSubset(uint key, out SubsetRef rows) => First.TryGetSubset(key, out rows);

        /// <summary>
        /// Looks up the rows holding the key at <paramref name="key"/>[0..arity) in a multi-column prober of one piece:
        /// egglog's <c>get_subset</c>.
        /// </summary>
        /// <param name="key">The array holding the key.</param>
        /// <param name="rows">The rows, nonempty and sorted; borrowed from the index.</param>
        /// <returns><c>true</c> if any of the atom's rows holds the key.</returns>
        public readonly bool TryGetSubset(uint[] key, out SubsetRef rows) => First.TryGetSubset(key, out rows);

    }

}

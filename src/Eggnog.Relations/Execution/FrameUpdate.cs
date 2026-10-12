namespace Eggnog.Relations
{

    /// <summary>
    /// One instruction of a <see cref="FrameUpdates"/> buffer: egglog's <c>UpdateInstr</c>
    /// (core-relations/src/free_join/frame_update.rs:27-35) as one flat struct, so the buffer is a single array.
    /// </summary>
    /// <remarks>
    /// The meaning of the fields depends on <see cref="Kind"/> (see <see cref="FrameUpdateKind"/>). Mutable so that a
    /// replayed entry can drop its node reference in place.
    /// </remarks>
    internal struct FrameUpdate
    {

        /// <summary>
        /// The kind of instruction.
        /// </summary>
        public FrameUpdateKind Kind;

        /// <summary>
        /// The layer of a <see cref="FrameUpdateKind.RefineAtomDense"/> refinement's rows (colored-contexts spec §9.5): 0
        /// at a root. A byte beside <see cref="Kind"/>, so the struct stays 24 bytes.
        /// </summary>
        public byte Layer;

        /// <summary>
        /// The variable or atom index.
        /// </summary>
        public int Id;

        /// <summary>
        /// The bound value (as raw bits), or the first row of a dense refinement.
        /// </summary>
        public int A;

        /// <summary>
        /// One past the last row of a dense refinement.
        /// </summary>
        public int B;

        /// <summary>
        /// The node of a <see cref="FrameUpdateKind.RefineAtomShared"/> or <see cref="FrameUpdateKind.RefineAtomOwned"/>.
        /// </summary>
        public TrieNode? Node;

    }

}

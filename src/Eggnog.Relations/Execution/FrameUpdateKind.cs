namespace Eggnog.Relations
{

    /// <summary>
    /// The kind of a <see cref="FrameUpdate"/>: egglog's <c>UpdateInstr</c> variants
    /// (core-relations/src/free_join/frame_update.rs:27-35), with <c>RefineAtom</c> split by who owns the node.
    /// </summary>
    internal enum FrameUpdateKind : byte
    {

        /// <summary>
        /// Bind variable <see cref="FrameUpdate.Id"/> to <see cref="FrameUpdate.A"/>.
        /// </summary>
        PushBinding,

        /// <summary>
        /// Refine atom <see cref="FrameUpdate.Id"/> to <see cref="FrameUpdate.Node"/>, a node cached by its parent.
        /// </summary>
        RefineAtomShared,

        /// <summary>
        /// Refine atom <see cref="FrameUpdate.Id"/> to <see cref="FrameUpdate.Node"/>, a node the frame created and
        /// owns: released after the frame's recursion, or when the frame is rolled back.
        /// </summary>
        RefineAtomOwned,

        /// <summary>
        /// Refine atom <see cref="FrameUpdate.Id"/> to the dense rows <c>[A, B)</c> without a node of its own.
        /// </summary>
        RefineAtomDense,

        /// <summary>
        /// The end of a frame: time to recurse.
        /// </summary>
        EndFrame,

    }

}

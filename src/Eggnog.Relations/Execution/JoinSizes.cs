using System.Runtime.CompilerServices;

namespace Eggnog.Relations
{

    /// <summary>
    /// The current sizes <see cref="StageOrdering"/> reorders by, read from a <see cref="JoinRun"/>: the row count of
    /// each atom's current trie node and the key count of each materialization (egglog's <c>estimate_size</c>,
    /// core-relations/src/free_join/execute.rs:2266-2276).
    /// </summary>
    /// <remarks>
    /// A struct, so the sort is specialized for it and the reads inline.
    /// </remarks>
    internal readonly struct JoinSizes : IStageSizeSource
    {

        readonly JoinRun run;

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="run">The join whose sizes are read.</param>
        public JoinSizes(JoinRun run)
        {
            this.run = run;
        }

        /// <inheritdoc />
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int GetAtomSize(AtomId atom) => run.GetAtomSize(atom.Index);

        /// <inheritdoc />
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int GetMaterializationSize(MatId mat) => run.GetMaterializationSize(mat.Index);

    }

}

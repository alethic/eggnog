using System;

namespace Eggnog.Relations
{

    /// <summary>
    /// Keeps the row the table already holds: egglog's <c>:merge old</c> (<c>ResolvedMergeFn::Old</c>,
    /// egglog-bridge/src/lib.rs:1184). The first row inserted for a key wins forever. Not a lattice merge: the winner
    /// depends on arrival order.
    /// </summary>
    internal sealed class KeepCurrentMerge : MergeFunction
    {

        /// <summary>
        /// Gets the shared instance; the merge has no state.
        /// </summary>
        public static KeepCurrentMerge Instance { get; } = new();

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        public KeepCurrentMerge()
            : base(false)
        {

        }

        /// <inheritdoc />
        public override bool Merge(ExecutionState state, ReadOnlySpan<uint> current, ReadOnlySpan<uint> incoming, Span<uint> output)
        {
            return false;
        }

    }

}

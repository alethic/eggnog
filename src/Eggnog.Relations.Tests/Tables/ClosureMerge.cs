using System;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// A <see cref="MergeFunction"/> built from a closure over copies of the rows, like the <c>merge_fn</c> egglog's
    /// <c>fill_table</c> takes (core-relations/src/table_shortcuts.rs:30-54): <c>null</c> keeps the current row, any
    /// other result replaces it.
    /// </summary>
    sealed class ClosureMerge : MergeFunction
    {

        readonly Func<uint[], uint[], uint[]?> merge;

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="merge">Maps (current, incoming) to the new row, or <c>null</c>.</param>
        /// <param name="isLattice">The declared lattice flag.</param>
        public ClosureMerge(Func<uint[], uint[], uint[]?> merge, bool isLattice = false)
            : base(isLattice)
        {
            this.merge = merge;
        }

        /// <summary>
        /// Gets a merge that always takes the incoming row: egglog's <c>|_, new| Some(new.to_vec())</c>.
        /// </summary>
        public static ClosureMerge TakeNew { get; } = new((_, incoming) => incoming);

        /// <inheritdoc />
        public override bool Merge(ExecutionState state, ReadOnlySpan<uint> current, ReadOnlySpan<uint> incoming, Span<uint> output)
        {
            var result = merge(current.ToArray(), incoming.ToArray());
            if (result is null)
                return false;

            result.CopyTo(output);
            return true;
        }

    }

}

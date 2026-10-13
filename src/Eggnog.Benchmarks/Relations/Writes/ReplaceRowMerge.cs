using System;

namespace Eggnog.Relations.Benchmarks
{

    /// <summary>
    /// The merge function of egglog's <c>core-relations/benches/writes.rs</c>: the incoming row replaces the current one,
    /// and the merge reports a change when the two rows differ in any cell
    /// (<c>|_, old, new, out| { out.extend_from_slice(new); old != new }</c>).
    /// </summary>
    /// <remarks>
    /// Unlike <see cref="KeepIncomingMerge"/>, which compares one value column, this compares whole rows, as the Rust
    /// closure does. Like the closure it writes the output even when it reports no change. Not a lattice merge: the
    /// winner depends on arrival order.
    /// </remarks>
    internal sealed class ReplaceRowMerge : MergeFunction
    {

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        public ReplaceRowMerge()
            : base(false)
        {

        }

        /// <inheritdoc />
        public override bool Merge(ExecutionState state, ReadOnlySpan<uint> current, ReadOnlySpan<uint> incoming, Span<uint> output)
        {
            incoming.CopyTo(output);
            return current.SequenceEqual(incoming) == false;
        }

    }

}

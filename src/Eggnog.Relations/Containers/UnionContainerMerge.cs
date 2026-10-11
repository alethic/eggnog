using System;

namespace Eggnog.Relations
{

    /// <summary>
    /// The bridge's container merge: when two equal containers carry different ids, stage the union
    /// <c>[current, incoming, ts]</c> into the union-find table and keep the smaller id. egglog-bridge's closure in
    /// <c>register_container_ty</c> (egglog-bridge/src/lib.rs:242-257).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Keeping the minimum matches the union-find's union-by-min (and <see cref="UnionIdMerge"/>), so the id the
    /// hash-cons keeps is the one the union-find makes canonical once the staged union merges. Equal ids stage nothing.
    /// </para>
    /// <para>
    /// The union's timestamp is the current value of the timestamp counter when one is given (the bridge reads its
    /// <c>timestamp_counter</c>), otherwise the state's <see cref="ExecutionState.Timestamp"/>. Stateless; safe to call
    /// from parallel rebuild tasks.
    /// </para>
    /// </remarks>
    internal sealed class UnionContainerMerge : ContainerMergeFunction
    {

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="unionFind">The union-find table unions are staged into.</param>
        /// <param name="timestampCounter">The counter whose value stamps the union, or <c>null</c> to use the state's
        /// timestamp.</param>
        public UnionContainerMerge(TableId unionFind, CounterId? timestampCounter = null)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(unionFind.Rep, nameof(unionFind));
            if (timestampCounter is { } counter)
                ArgumentOutOfRangeException.ThrowIfNegative(counter.Rep, nameof(timestampCounter));

            UnionFind = unionFind;
            TimestampCounter = timestampCounter;
        }

        /// <summary>
        /// Gets the union-find table unions are staged into.
        /// </summary>
        public TableId UnionFind { get; }

        /// <summary>
        /// Gets the counter whose value stamps the union, or <c>null</c> when the state's timestamp does.
        /// </summary>
        public CounterId? TimestampCounter { get; }

        /// <inheritdoc />
        public override uint Merge(ExecutionState state, uint current, uint incoming)
        {
            if (current == incoming)
                return current;

            var timestamp = TimestampCounter is { } counter ? (uint)state.ReadCounter(counter) : state.Timestamp;
            Span<uint> union = stackalloc uint[3];
            union[0] = current;
            union[1] = incoming;
            union[2] = timestamp;
            state.StageInsert(UnionFind, union);

            // THIS MUST MATCH THE UNION-FIND's tie-break (egglog-bridge/src/lib.rs:252).
            return Math.Min(current, incoming);
        }

    }

}

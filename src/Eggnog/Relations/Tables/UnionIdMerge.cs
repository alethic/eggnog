using System;

namespace Eggnog.Relations
{

    /// <summary>
    /// Merges two e-class ids by unioning them: egglog's <c>UnionId</c> merge (<c>ResolvedMergeFn::UnionId</c>,
    /// egglog-bridge/src/lib.rs:1193-1202). When the values differ it stages the row <c>[current, incoming, ts]</c>
    /// into the union-find table and keeps the smaller id, which is the id the union-find keeps canonical. A lattice
    /// merge.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The row changes only when the incoming id is the smaller one; otherwise the merge returns <c>false</c> but has
    /// still staged the union, so <see cref="ExecutionState.Changed"/> is set and the merge loop keeps going, as in
    /// egglog. The output is the incoming row, keeping its timestamp.
    /// </para>
    /// <para>
    /// The union's timestamp comes from <see cref="TimestampColumn"/> of the incoming row when there is one (the
    /// bridge's <c>new[ts_col]</c>), otherwise from <see cref="ExecutionState.Timestamp"/>.
    /// </para>
    /// </remarks>
    internal sealed class UnionIdMerge : MergeFunction
    {

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="valueColumn">The column holding the ids to union.</param>
        /// <param name="unionFind">The union-find table that unions are staged into.</param>
        /// <param name="timestampColumn">The incoming row's timestamp column, or <c>null</c> to use the state's.</param>
        public UnionIdMerge(ColumnId valueColumn, TableId unionFind, ColumnId? timestampColumn = null)
            : base(true)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(valueColumn.Rep, nameof(valueColumn));
            if (timestampColumn is { } ts)
                ArgumentOutOfRangeException.ThrowIfNegative(ts.Rep, nameof(timestampColumn));

            ValueColumn = valueColumn.Rep;
            UnionFind = unionFind;
            TimestampColumn = timestampColumn?.Rep ?? -1;
        }

        /// <summary>
        /// Gets the column holding the ids to union.
        /// </summary>
        public int ValueColumn { get; }

        /// <summary>
        /// Gets the union-find table that unions are staged into.
        /// </summary>
        public TableId UnionFind { get; }

        /// <summary>
        /// Gets the incoming row's timestamp column, or -1 to use <see cref="ExecutionState.Timestamp"/>.
        /// </summary>
        public int TimestampColumn { get; }

        /// <inheritdoc />
        public override bool Merge(ExecutionState state, ReadOnlySpan<uint> current, ReadOnlySpan<uint> incoming, Span<uint> output)
        {
            var cur = current[ValueColumn];
            var inc = incoming[ValueColumn];
            if (cur == inc)
                return false;

            Span<uint> union = stackalloc uint[3];
            union[0] = cur;
            union[1] = inc;
            union[2] = TimestampColumn >= 0 ? incoming[TimestampColumn] : state.Timestamp;
            state.StageInsert(UnionFind, union);

            // Keep the minimum, as the union-find does: THIS MUST MATCH THE UNION-FIND (egglog-bridge/src/lib.rs:1196).
            if (inc > cur)
                return false;

            incoming.CopyTo(output);
            return true;
        }

    }

}

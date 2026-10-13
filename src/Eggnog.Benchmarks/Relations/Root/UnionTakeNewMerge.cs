using System;

namespace Eggnog.Relations.Benchmarks
{

    /// <summary>
    /// The merge of egglog's <c>basic_math_egraph</c> test tables (core-relations/src/tests.rs:969-1009), the same as
    /// the test project's <c>UnionTakeNewMerge</c>: when the id in <see cref="ValueColumn"/> differs, stage the union
    /// <c>[current, incoming, incoming ts]</c> into the union-find and take the incoming row ("picking b as the presumed
    /// winner").
    /// </summary>
    /// <remarks>
    /// Not a lattice merge: which row survives depends on arrival order. Allocation-free: the union row lives on the
    /// stack.
    /// </remarks>
    internal sealed class UnionTakeNewMerge : MergeFunction
    {

        readonly TableId unionFind;
        readonly int valueColumn;
        readonly int timestampColumn;

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="unionFind">The union-find table.</param>
        /// <param name="valueColumn">The id column.</param>
        /// <param name="timestampColumn">The timestamp column.</param>
        public UnionTakeNewMerge(TableId unionFind, int valueColumn, int timestampColumn)
            : base(false)
        {
            this.unionFind = unionFind;
            this.valueColumn = valueColumn;
            this.timestampColumn = timestampColumn;
        }

        /// <summary>
        /// Gets the column holding the id.
        /// </summary>
        public int ValueColumn => valueColumn;

        /// <inheritdoc />
        public override bool Merge(ExecutionState state, ReadOnlySpan<uint> current, ReadOnlySpan<uint> incoming, Span<uint> output)
        {
            if (current[valueColumn] == incoming[valueColumn])
                return false;

            state.StageInsert(unionFind, [current[valueColumn], incoming[valueColumn], incoming[timestampColumn]]);
            incoming.CopyTo(output);
            return true;
        }

    }

}

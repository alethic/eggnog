using System;
using System.Runtime.CompilerServices;

namespace Eggnog.Relations
{

    /// <summary>
    /// A child context's <see cref="Rebuilder"/>: rewrites chosen columns of the child's own rows to their canonical ids
    /// in the child, through its layered union-find (colored-contexts spec §5.5). Provided by
    /// <see cref="OverlayDisplacedTable.GetRebuilder"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The same loops as the root's <see cref="Canonicalizer"/> (<see cref="CanonicalizeLoops"/>), run with
    /// <see cref="LayeredIdFinder"/>: a find is the root's find plus one probe per non-empty overlay level, and never
    /// writes, so a child's rule tasks may rebuild concurrently while its union-find does not change.
    /// </para>
    /// <para>
    /// <see cref="HintColumn"/> is column 0 of the child's union-find table, its displaced journal: the ids that stopped
    /// being canonical in the child since a layer's last rebuild are exactly the journal's rows appended since (its own
    /// losers, the canonicals re-keying lost, and the imports of parent displacements), which gives a layer egglog's
    /// incremental rebuild (§7.3, X9).
    /// </para>
    /// </remarks>
    internal sealed class OverlayCanonicalizer : Rebuilder
    {

        readonly LayeredUnionFind finder;
        readonly int[] columns;

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="finder">The child's layered union-find.</param>
        /// <param name="columns">The columns to canonicalize.</param>
        public OverlayCanonicalizer(LayeredUnionFind finder, ReadOnlySpan<ColumnId> columns)
        {
            ArgumentNullException.ThrowIfNull(finder);

            this.finder = finder;
            this.columns = CanonicalizeLoops.Columns(columns);
        }

        /// <inheritdoc />
        public override ColumnId? HintColumn => new ColumnId(0);

        /// <inheritdoc />
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public override uint RebuildValue(uint value) => finder.Find(value);

        /// <inheritdoc />
        /// <remarks>egglog's <c>rebuild_buf</c> with the child's find. O(rows × columns × find).</remarks>
        public override void RebuildBuffer(RowBuffer buffer, int start, int end, TaggedRowBuffer output, ExecutionState state)
        {
            CanonicalizeLoops.RebuildBuffer(new LayeredIdFinder(finder), columns, buffer, start, end, output);
        }

        /// <inheritdoc />
        /// <remarks>egglog's <c>rebuild_subset</c> with the child's find.</remarks>
        public override void RebuildSubset(Table table, SubsetRef subset, TaggedRowBuffer output, ExecutionState state)
        {
            CanonicalizeLoops.RebuildSubset(new LayeredIdFinder(finder), columns, table, subset, output);
        }

        /// <summary>
        /// Canonicalizes, in place, the configured columns of one row. O(columns × find).
        /// </summary>
        /// <param name="values">The row's cells.</param>
        /// <returns><c>true</c> if any column changed.</returns>
        public bool RebuildRow(Span<uint> values) => CanonicalizeLoops.RebuildRow(new LayeredIdFinder(finder), columns, values);

        /// <inheritdoc />
        /// <remarks>egglog's <c>rebuild_slice</c> with the child's find.</remarks>
        public override bool RebuildSlice(Span<uint> values) => CanonicalizeLoops.RebuildSlice(new LayeredIdFinder(finder), values);

    }

}

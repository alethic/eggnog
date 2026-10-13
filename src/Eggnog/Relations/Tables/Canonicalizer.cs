using System;
using System.Runtime.CompilerServices;

namespace Eggnog.Relations
{

    /// <summary>
    /// The union-find's <see cref="Rebuilder"/>: rewrites chosen columns of another table's rows to their canonical
    /// ids. egglog's <c>Canonicalizer</c> (core-relations/src/uf/mod.rs:62-196).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="HintColumn"/> is column 0 of the union-find table: the values that stopped being canonical are
    /// exactly the displaced ids of the rows appended since a table's last rebuild, which is what makes incremental
    /// rebuild possible. Canonical ids come from <see cref="DisplacedUnionFind.FindNaive"/>, which never writes, so
    /// many threads may rebuild at once while the union-find does not change.
    /// </para>
    /// <para>
    /// The loops live in <see cref="CanonicalizeLoops"/>, shared with a child context's <see cref="OverlayCanonicalizer"/>
    /// (colored-contexts spec §5.5); this class runs them with <see cref="RootIdFinder"/>, a struct the JIT specializes,
    /// so the machine code is the code these loops had here before. <see cref="RebuildBuffer"/> is specialized for one,
    /// two and three columns, as egglog's is, and skips tombstones (egglog rewrites them and its caller drops them).
    /// </para>
    /// </remarks>
    internal sealed class Canonicalizer : Rebuilder
    {

        readonly DisplacedUnionFind uf;
        readonly int[] columns;

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="uf">The union-find.</param>
        /// <param name="columns">The columns to canonicalize.</param>
        public Canonicalizer(DisplacedUnionFind uf, ReadOnlySpan<ColumnId> columns)
        {
            ArgumentNullException.ThrowIfNull(uf);

            this.uf = uf;
            this.columns = CanonicalizeLoops.Columns(columns);
        }

        /// <inheritdoc />
        public override ColumnId? HintColumn => new ColumnId(0);

        /// <inheritdoc />
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public override uint RebuildValue(uint value) => uf.FindNaive(value);

        /// <inheritdoc />
        /// <remarks>egglog's <c>rebuild_buf</c> (core-relations/src/uf/mod.rs:74-161). O(rows × columns × find).</remarks>
        public override void RebuildBuffer(RowBuffer buffer, int start, int end, TaggedRowBuffer output, ExecutionState state)
        {
            CanonicalizeLoops.RebuildBuffer(new RootIdFinder(uf), columns, buffer, start, end, output);
        }

        /// <inheritdoc />
        /// <remarks>
        /// egglog's <c>rebuild_subset</c> (core-relations/src/uf/mod.rs:162-186): scans the subset's live rows into
        /// <paramref name="output"/>, canonicalizes them there, and tombstones the copies that did not change.
        /// </remarks>
        public override void RebuildSubset(Table table, SubsetRef subset, TaggedRowBuffer output, ExecutionState state)
        {
            CanonicalizeLoops.RebuildSubset(new RootIdFinder(uf), columns, table, subset, output);
        }

        /// <summary>
        /// Canonicalizes, in place, the configured columns of one row. O(columns × find).
        /// </summary>
        /// <param name="values">The row's cells.</param>
        /// <returns><c>true</c> if any column changed.</returns>
        public bool RebuildRow(Span<uint> values) => CanonicalizeLoops.RebuildRow(new RootIdFinder(uf), columns, values);

        /// <inheritdoc />
        /// <remarks>
        /// egglog's <c>rebuild_slice</c> (core-relations/src/uf/mod.rs:187-195): canonicalizes every value of the slice,
        /// whatever the configured columns (container contents are all ids).
        /// </remarks>
        public override bool RebuildSlice(Span<uint> values) => CanonicalizeLoops.RebuildSlice(new RootIdFinder(uf), values);

    }

}

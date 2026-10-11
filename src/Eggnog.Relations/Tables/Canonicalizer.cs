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
    /// <see cref="RebuildBuffer"/> is specialized for one, two and three columns, as egglog's is, and skips tombstones
    /// (egglog rewrites them and its caller drops them).
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
            this.columns = new int[columns.Length];
            for (var i = 0; i < columns.Length; i++)
            {
                ArgumentOutOfRangeException.ThrowIfNegative(columns[i].Rep, nameof(columns));
                this.columns[i] = columns[i].Rep;
            }
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
            ArgumentNullException.ThrowIfNull(buffer);
            ArgumentNullException.ThrowIfNull(output);
            if (start >= end)
                return;

            if (start < 0 || end > buffer.Count)
                throw new ArgumentOutOfRangeException(nameof(end), end, $"The rows [{start}, {end}) are not all in the buffer of {buffer.Count} rows.");

            foreach (var column in columns)
                if (column >= buffer.Arity)
                    throw new ArgumentOutOfRangeException(nameof(buffer), column, "A column to canonicalize is not part of the rows.");

            switch (columns.Length)
            {
                case 0:
                    return;
                case 1:
                    RebuildOne(buffer, start, end, output);
                    return;
                case 2:
                    RebuildTwo(buffer, start, end, output);
                    return;
                case 3:
                    RebuildThree(buffer, start, end, output);
                    return;
                default:
                    RebuildMany(buffer, start, end, output);
                    return;
            }
        }

        /// <inheritdoc />
        /// <remarks>
        /// egglog's <c>rebuild_subset</c> (core-relations/src/uf/mod.rs:162-186): scans the subset's live rows into
        /// <paramref name="output"/>, canonicalizes them there, and tombstones the copies that did not change.
        /// </remarks>
        public override void RebuildSubset(Table table, SubsetRef subset, TaggedRowBuffer output, ExecutionState state)
        {
            ArgumentNullException.ThrowIfNull(table);
            ArgumentNullException.ThrowIfNull(output);

            var first = output.Count;
            var next = table.ScanBounded(subset, 0, int.MaxValue, output);
            if (next >= 0)
                throw new InvalidOperationException("A scan of the whole subset stopped early.");

            for (var i = first; i < output.Count; i++)
            {
                var row = output.GetMutableRow(i, out _);
                if (RebuildRow(row) == false)
                    output.SetStale(i);
            }
        }

        /// <summary>
        /// Canonicalizes, in place, the configured columns of one row. O(columns × find).
        /// </summary>
        /// <param name="values">The row's cells.</param>
        /// <returns><c>true</c> if any column changed.</returns>
        public bool RebuildRow(Span<uint> values)
        {
            var changed = false;
            foreach (var column in columns)
            {
                var value = values[column];
                var canonical = uf.FindNaive(value);
                values[column] = canonical;
                changed |= canonical != value;
            }

            return changed;
        }

        /// <inheritdoc />
        /// <remarks>
        /// egglog's <c>rebuild_slice</c> (core-relations/src/uf/mod.rs:187-195): canonicalizes every value of the slice,
        /// whatever the configured columns (container contents are all ids).
        /// </remarks>
        public override bool RebuildSlice(Span<uint> values)
        {
            var changed = false;
            for (var i = 0; i < values.Length; i++)
            {
                var value = values[i];
                var canonical = uf.FindNaive(value);
                values[i] = canonical;
                changed |= canonical != value;
            }

            return changed;
        }

        void RebuildOne(RowBuffer buffer, int start, int end, TaggedRowBuffer output)
        {
            var arity = buffer.Arity;
            var cells = buffer.Data;
            var c = columns[0];
            for (var row = start; row < end; row++)
            {
                var offset = row * arity;
                if (cells[offset] == Value.StaleRep)
                    continue;

                var value = cells[offset + c];
                var canonical = uf.FindNaive(value);
                if (canonical == value)
                    continue;

                var added = output.AddRow(row, new ReadOnlySpan<uint>(cells, offset, arity));
                output.GetMutableRow(added, out _)[c] = canonical;
            }
        }

        void RebuildTwo(RowBuffer buffer, int start, int end, TaggedRowBuffer output)
        {
            var arity = buffer.Arity;
            var cells = buffer.Data;
            var c1 = columns[0];
            var c2 = columns[1];
            for (var row = start; row < end; row++)
            {
                var offset = row * arity;
                if (cells[offset] == Value.StaleRep)
                    continue;

                var v1 = cells[offset + c1];
                var v2 = cells[offset + c2];
                var ca1 = uf.FindNaive(v1);
                var ca2 = uf.FindNaive(v2);
                if (ca1 == v1 && ca2 == v2)
                    continue;

                var added = output.AddRow(row, new ReadOnlySpan<uint>(cells, offset, arity));
                var copy = output.GetMutableRow(added, out _);
                copy[c1] = ca1;
                copy[c2] = ca2;
            }
        }

        void RebuildThree(RowBuffer buffer, int start, int end, TaggedRowBuffer output)
        {
            var arity = buffer.Arity;
            var cells = buffer.Data;
            var c1 = columns[0];
            var c2 = columns[1];
            var c3 = columns[2];
            for (var row = start; row < end; row++)
            {
                var offset = row * arity;
                if (cells[offset] == Value.StaleRep)
                    continue;

                var v1 = cells[offset + c1];
                var v2 = cells[offset + c2];
                var v3 = cells[offset + c3];
                var ca1 = uf.FindNaive(v1);
                var ca2 = uf.FindNaive(v2);
                var ca3 = uf.FindNaive(v3);
                if (ca1 == v1 && ca2 == v2 && ca3 == v3)
                    continue;

                var added = output.AddRow(row, new ReadOnlySpan<uint>(cells, offset, arity));
                var copy = output.GetMutableRow(added, out _);
                copy[c1] = ca1;
                copy[c2] = ca2;
                copy[c3] = ca3;
            }
        }

        void RebuildMany(RowBuffer buffer, int start, int end, TaggedRowBuffer output)
        {
            var arity = buffer.Arity;
            var cells = buffer.Data;
            Span<uint> scratch = arity <= 64 ? stackalloc uint[arity] : new uint[arity];
            for (var row = start; row < end; row++)
            {
                var offset = row * arity;
                if (cells[offset] == Value.StaleRep)
                    continue;

                new ReadOnlySpan<uint>(cells, offset, arity).CopyTo(scratch);
                if (RebuildRow(scratch))
                    output.AddRow(row, scratch);
            }
        }

    }

}

using System;
using System.Runtime.CompilerServices;

namespace Eggnog.Relations
{

    /// <summary>
    /// The per-row loops of the canonicalizing rebuilders, generic over the find they apply (colored-contexts spec
    /// §5.5): egglog's <c>Canonicalizer</c> bodies (core-relations/src/uf/mod.rs:62-196), moved here verbatim from
    /// <see cref="Canonicalizer"/> with <c>uf.FindNaive</c> replaced by <c>finder.Find</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The finder is a struct type parameter (<see cref="IIdFinder"/>), so the JIT compiles each loop once per finder
    /// with the find inlined: <see cref="Canonicalizer"/>'s loops over <see cref="RootIdFinder"/> are the machine code
    /// they were before contexts existed, and <see cref="OverlayCanonicalizer"/>'s over <see cref="LayeredIdFinder"/> add
    /// only the overlay probes. Nothing is added per row.
    /// </para>
    /// <para>
    /// <see cref="RebuildBuffer{TF}"/> is specialized for one, two and three columns, as egglog's is, and skips
    /// tombstones (egglog rewrites them and its caller drops them). Every loop only reads the union-find, so many threads
    /// may rebuild at once while it does not change.
    /// </para>
    /// </remarks>
    internal static class CanonicalizeLoops
    {

        /// <summary>
        /// Converts and checks the columns a canonicalizer rewrites.
        /// </summary>
        /// <param name="columns">The columns.</param>
        /// <returns>Their indexes.</returns>
        /// <exception cref="ArgumentOutOfRangeException">A column is negative.</exception>
        public static int[] Columns(ReadOnlySpan<ColumnId> columns)
        {
            var result = new int[columns.Length];
            for (var i = 0; i < columns.Length; i++)
            {
                ArgumentOutOfRangeException.ThrowIfNegative(columns[i].Rep, nameof(columns));
                result[i] = columns[i].Rep;
            }

            return result;
        }

        /// <summary>
        /// Appends to <paramref name="output"/>, tagged with their row ids, the live rows of <c>[start, end)</c> of
        /// <paramref name="buffer"/> that the find changes, rewritten: egglog's <c>rebuild_buf</c>
        /// (core-relations/src/uf/mod.rs:74-161). O(rows × columns × find).
        /// </summary>
        /// <typeparam name="TF">The finder.</typeparam>
        /// <param name="finder">The find to apply.</param>
        /// <param name="columns">The columns to canonicalize.</param>
        /// <param name="buffer">The rows.</param>
        /// <param name="start">The first row.</param>
        /// <param name="end">One past the last row.</param>
        /// <param name="output">Receives the rewritten rows.</param>
        /// <exception cref="ArgumentOutOfRangeException">The rows are not all in the buffer, or a column is not part of
        /// them.</exception>
        public static void RebuildBuffer<TF>(TF finder, int[] columns, RowBuffer buffer, int start, int end, TaggedRowBuffer output)
            where TF : struct, IIdFinder
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
                    RebuildOne(finder, columns, buffer, start, end, output);
                    return;
                case 2:
                    RebuildTwo(finder, columns, buffer, start, end, output);
                    return;
                case 3:
                    RebuildThree(finder, columns, buffer, start, end, output);
                    return;
                default:
                    RebuildMany(finder, columns, buffer, start, end, output);
                    return;
            }
        }

        /// <summary>
        /// Scans the live rows of <paramref name="subset"/> of <paramref name="table"/> into <paramref name="output"/>,
        /// canonicalizes them there, and tombstones the copies the find did not change: egglog's <c>rebuild_subset</c>
        /// (core-relations/src/uf/mod.rs:162-186).
        /// </summary>
        /// <typeparam name="TF">The finder.</typeparam>
        /// <param name="finder">The find to apply.</param>
        /// <param name="columns">The columns to canonicalize.</param>
        /// <param name="table">The table being rebuilt.</param>
        /// <param name="subset">The rows to rewrite.</param>
        /// <param name="output">Receives the rows.</param>
        public static void RebuildSubset<TF>(TF finder, int[] columns, Table table, SubsetRef subset, TaggedRowBuffer output)
            where TF : struct, IIdFinder
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
                if (RebuildRow(finder, columns, row) == false)
                    output.SetStale(i);
            }
        }

        /// <summary>
        /// Canonicalizes, in place, the configured columns of one row. O(columns × find).
        /// </summary>
        /// <typeparam name="TF">The finder.</typeparam>
        /// <param name="finder">The find to apply.</param>
        /// <param name="columns">The columns to canonicalize.</param>
        /// <param name="values">The row's cells.</param>
        /// <returns><c>true</c> if any column changed.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool RebuildRow<TF>(TF finder, int[] columns, Span<uint> values)
            where TF : struct, IIdFinder
        {
            var changed = false;
            foreach (var column in columns)
            {
                var value = values[column];
                var canonical = finder.Find(value);
                values[column] = canonical;
                changed |= canonical != value;
            }

            return changed;
        }

        /// <summary>
        /// Canonicalizes every value of <paramref name="values"/> in place, whatever the configured columns (container
        /// contents are all ids): egglog's <c>rebuild_slice</c> (core-relations/src/uf/mod.rs:187-195).
        /// </summary>
        /// <typeparam name="TF">The finder.</typeparam>
        /// <param name="finder">The find to apply.</param>
        /// <param name="values">The values.</param>
        /// <returns><c>true</c> if any value changed.</returns>
        public static bool RebuildSlice<TF>(TF finder, Span<uint> values)
            where TF : struct, IIdFinder
        {
            var changed = false;
            for (var i = 0; i < values.Length; i++)
            {
                var value = values[i];
                var canonical = finder.Find(value);
                values[i] = canonical;
                changed |= canonical != value;
            }

            return changed;
        }

        static void RebuildOne<TF>(TF finder, int[] columns, RowBuffer buffer, int start, int end, TaggedRowBuffer output)
            where TF : struct, IIdFinder
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
                var canonical = finder.Find(value);
                if (canonical == value)
                    continue;

                var added = output.AddRow(row, new ReadOnlySpan<uint>(cells, offset, arity));
                output.GetMutableRow(added, out _)[c] = canonical;
            }
        }

        static void RebuildTwo<TF>(TF finder, int[] columns, RowBuffer buffer, int start, int end, TaggedRowBuffer output)
            where TF : struct, IIdFinder
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
                var ca1 = finder.Find(v1);
                var ca2 = finder.Find(v2);
                if (ca1 == v1 && ca2 == v2)
                    continue;

                var added = output.AddRow(row, new ReadOnlySpan<uint>(cells, offset, arity));
                var copy = output.GetMutableRow(added, out _);
                copy[c1] = ca1;
                copy[c2] = ca2;
            }
        }

        static void RebuildThree<TF>(TF finder, int[] columns, RowBuffer buffer, int start, int end, TaggedRowBuffer output)
            where TF : struct, IIdFinder
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
                var ca1 = finder.Find(v1);
                var ca2 = finder.Find(v2);
                var ca3 = finder.Find(v3);
                if (ca1 == v1 && ca2 == v2 && ca3 == v3)
                    continue;

                var added = output.AddRow(row, new ReadOnlySpan<uint>(cells, offset, arity));
                var copy = output.GetMutableRow(added, out _);
                copy[c1] = ca1;
                copy[c2] = ca2;
                copy[c3] = ca3;
            }
        }

        static void RebuildMany<TF>(TF finder, int[] columns, RowBuffer buffer, int start, int end, TaggedRowBuffer output)
            where TF : struct, IIdFinder
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
                if (RebuildRow(finder, columns, scratch))
                    output.AddRow(row, scratch);
            }
        }

    }

}

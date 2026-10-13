using System;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Eggnog.Relations
{

    internal sealed partial class SortedWritesTable
    {

        /// <summary>
        /// Visits the rows at positions <c>[start, start + count)</c> of <paramref name="subset"/> that are live and
        /// satisfy every constraint, and returns the position to resume from, or -1 when the subset is exhausted:
        /// egglog's <c>scan_generic_bounded</c> (core-relations/src/table/mod.rs:387-440).
        /// </summary>
        /// <remarks>
        /// <para>
        /// Positions count entries of the subset, not emitted rows (<see cref="SubsetRef.IterBounded{TVisitor}"/>), so
        /// a chunk may emit fewer than <paramref name="count"/> rows. The subset is checked against the table once,
        /// then rows are read without bounds checks. Without constraints and without tombstones the loop does no
        /// per-row test at all.
        /// </para>
        /// <para>
        /// Tombstones are skipped whenever the table has any, with or without constraints. egglog evaluates constraints
        /// on raw rows without that check (mod.rs:429-439, 1018-1025), so a deleted or superseded row whose remaining
        /// cells satisfy a slow constraint leaks into the join; a column-0 constraint such as <c>c0 &gt; v</c> even
        /// matches the tombstone marker itself.
        /// </para>
        /// <para>
        /// <see cref="Table.ScanGenericBounded{TSink}(SubsetRef, int, int, ReadOnlySpan{Constraint}, ref TSink)"/>, which
        /// this hides, dispatches here; the contract is the same.
        /// </para>
        /// </remarks>
        /// <typeparam name="TSink">The sink type.</typeparam>
        /// <param name="subset">The rows to scan; within the table's physical rows.</param>
        /// <param name="start">The first position.</param>
        /// <param name="count">The number of positions to visit.</param>
        /// <param name="constraints">The constraints rows must satisfy.</param>
        /// <param name="sink">Receives each row with its row id; must not keep the span.</param>
        /// <returns>The next position, or -1.</returns>
        public new int ScanGenericBounded<TSink>(SubsetRef subset, int start, int count, ReadOnlySpan<Constraint> constraints, ref TSink sink)
            where TSink : struct, IRowSink
        {
            ArgumentOutOfRangeException.ThrowIfNegative(start);
            ArgumentOutOfRangeException.ThrowIfNegative(count);

            if (subset.TryGetBounds(out var bounds) == false)
                return -1;

            if (bounds.End > data.Count || bounds.Start < 0)
                ThrowSubsetOutOfRange(bounds.End);

            // Positions [from, stop) of the subset, clipped, with SubsetRef.IterBounded's resume rules.
            var size = subset.Size;
            var stop = (int)Math.Min((long)start + count, size);
            var rows = subset.Rows;
            if (rows is null)
            {
                if (start >= size)
                    return -1;
            }
            else if (start > stop)
            {
                throw new ArgumentOutOfRangeException(nameof(start), start, $"Position {start} is past the end of the chunk {stop}.");
            }

            ref var cells = ref MemoryMarshal.GetArrayDataReference(data.Data);
            var arity = Arity;
            var checkStale = staleRows > 0;
            if (constraints.IsEmpty && checkStale == false)
            {
                // Fast path: no tombstones, nothing to test.
                if (rows is null)
                {
                    for (int row = subset.Lo + start, last = subset.Lo + stop; row < last; row++)
                        sink.AddRow(row, MemoryMarshal.CreateReadOnlySpan(ref Unsafe.Add(ref cells, row * arity), arity));
                }
                else
                {
                    foreach (var row in new ReadOnlySpan<int>(rows, subset.Lo + start, stop - start))
                        sink.AddRow(row, MemoryMarshal.CreateReadOnlySpan(ref Unsafe.Add(ref cells, row * arity), arity));
                }
            }
            else if (rows is null)
            {
                for (int row = subset.Lo + start, last = subset.Lo + stop; row < last; row++)
                    EmitIfMatches(ref cells, arity, row, checkStale, constraints, ref sink);
            }
            else
            {
                foreach (var row in new ReadOnlySpan<int>(rows, subset.Lo + start, stop - start))
                    EmitIfMatches(ref cells, arity, row, checkStale, constraints, ref sink);
            }

            return stop < size ? stop : -1;
        }

        /// <summary>
        /// Visits the rows at positions <c>[start, start + count)</c> of <paramref name="subset"/> that a view sees (live
        /// and not in <paramref name="hidden"/>) and that satisfy every constraint. It hides
        /// <see cref="Table.ScanGenericBounded{TSink}(SubsetRef, int, int, ReadOnlySpan{Constraint}, HiddenRows, ref TSink)"/>,
        /// the hidden-aware scan of the base class, which dispatches here.
        /// </summary>
        /// <remarks>
        /// With <see cref="HiddenRows.None"/> this is the plain scan,
        /// <see cref="ScanGenericBounded{TSink}(SubsetRef, int, int, ReadOnlySpan{Constraint}, ref TSink)"/>, unchanged.
        /// Otherwise each visited row is tested for a tombstone (when the table has any), then against the hidden sets by a
        /// <see cref="HiddenCursor"/> walk (rows of a subset ascend, so a dense chunk costs amortized O(1) per row), then
        /// against the constraints, in separate loops so the plain path keeps its shape.
        /// </remarks>
        /// <typeparam name="TSink">The sink type.</typeparam>
        /// <param name="subset">The rows to scan; within the table's physical rows.</param>
        /// <param name="start">The first position.</param>
        /// <param name="count">The number of positions to visit.</param>
        /// <param name="constraints">The constraints rows must satisfy.</param>
        /// <param name="hidden">The rows the view hides; sealed.</param>
        /// <param name="sink">Receives each visible row with its row id; must not keep the span.</param>
        /// <returns>The next position, or -1.</returns>
        public new int ScanGenericBounded<TSink>(SubsetRef subset, int start, int count, ReadOnlySpan<Constraint> constraints, HiddenRows hidden, ref TSink sink)
            where TSink : struct, IRowSink
        {
            if (hidden.IsNone)
                return ScanGenericBounded(subset, start, count, constraints, ref sink);

            return ScanGenericBoundedHidden(subset, start, count, constraints, hidden, ref sink);
        }

        /// <summary>
        /// Visits every live row of <paramref name="subset"/>: egglog's <c>scan_generic</c>
        /// (core-relations/src/table/mod.rs:356-385). Hides <see cref="Table.ScanGeneric{TSink}(SubsetRef, ref TSink)"/>,
        /// which dispatches here.
        /// </summary>
        /// <typeparam name="TSink">The sink type.</typeparam>
        /// <param name="subset">The rows to scan; within the table's physical rows.</param>
        /// <param name="sink">Receives each row with its row id; must not keep the span.</param>
        public new void ScanGeneric<TSink>(SubsetRef subset, ref TSink sink)
            where TSink : struct, IRowSink
        {
            ScanGenericBounded(subset, 0, int.MaxValue, ReadOnlySpan<Constraint>.Empty, ref sink);
        }

        /// <summary>
        /// Visits every row of <paramref name="subset"/> a view sees: live and not in <paramref name="hidden"/>. Hides
        /// <see cref="Table.ScanGeneric{TSink}(SubsetRef, HiddenRows, ref TSink)"/>, which dispatches here.
        /// </summary>
        /// <typeparam name="TSink">The sink type.</typeparam>
        /// <param name="subset">The rows to scan; within the table's physical rows.</param>
        /// <param name="hidden">The rows the view hides; sealed.</param>
        /// <param name="sink">Receives each visible row with its row id; must not keep the span.</param>
        public new void ScanGeneric<TSink>(SubsetRef subset, HiddenRows hidden, ref TSink sink)
            where TSink : struct, IRowSink
        {
            ScanGenericBounded(subset, 0, int.MaxValue, ReadOnlySpan<Constraint>.Empty, hidden, ref sink);
        }

        /// <inheritdoc />
        /// <remarks>
        /// egglog's <c>refine_one</c> (core-relations/src/table/mod.rs:511-516): keeps the rows that are live and satisfy
        /// the constraint, by <see cref="Subset.Retain{TFilter}(ref TFilter)"/>. O(subset size).
        /// </remarks>
        public override Subset RefineOne(Subset subset, Constraint constraint)
        {
            if ((uint)constraint.Column >= (uint)Arity || (constraint.Kind == ConstraintKind.Eq && (uint)constraint.OtherColumn >= (uint)Arity))
                throw new ArgumentOutOfRangeException(nameof(constraint), constraint, $"The table has {Arity} columns.");

            if (subset.TryGetBounds(out var bounds) && bounds.End > data.Count)
                ThrowSubsetOutOfRange(bounds.End);

            var filter = new LiveConstraintFilter(data.Data, Arity, constraint);
            subset.Retain(ref filter);
            return subset;
        }

        /// <inheritdoc />
        /// <remarks>
        /// <see cref="Table.RefineLive"/> with <see cref="HiddenRows.None"/>; otherwise one
        /// <see cref="Subset.Retain{TFilter}(ref TFilter)"/> with a <see cref="VisibleRowFilter"/> (tombstone test, then a
        /// <see cref="HiddenCursor"/> walk). O(subset size).
        /// </remarks>
        public override Subset RefineVisible(Subset subset, HiddenRows hidden)
        {
            if (hidden.IsNone)
                return RefineLive(subset);

            if (subset.TryGetBounds(out var bounds) && bounds.End > data.Count)
                ThrowSubsetOutOfRange(bounds.End);

            var filter = new VisibleRowFilter(this, hidden);
            subset.Retain(ref filter);
            return subset;
        }

        /// <inheritdoc />
        /// <remarks>
        /// egglog's <c>fast_subset</c> (core-relations/src/table/mod.rs:442-509): only constant constraints on the sort
        /// column are fast; each maps to one dense range found by <see cref="BinarySearchSortValue"/>. The range
        /// includes tombstones.
        /// </remarks>
        public override bool TryFastSubset(Constraint constraint, out Subset subset)
        {
            if (sortColumn < 0 || constraint.Kind == ConstraintKind.Eq || constraint.Column != sortColumn)
            {
                subset = default;
                return false;
            }

            var found = BinarySearchSortValue(constraint.Constant, out var runStart, out var runEnd);
            var end = data.Count;
            switch (constraint.Kind)
            {
                case ConstraintKind.EqConst:
                    subset = found ? Subset.Dense(runStart, runEnd) : Subset.Empty;
                    return true;
                case ConstraintKind.LtConst:
                    subset = Subset.Dense(0, runStart);
                    return true;
                case ConstraintKind.GtConst:
                    subset = Subset.Dense(runEnd, end);
                    return true;
                case ConstraintKind.LeConst:
                    subset = Subset.Dense(0, runEnd);
                    return true;
                default:
                    subset = Subset.Dense(runStart, end);
                    return true;
            }
        }

        /// <summary>
        /// Gets the rows whose sort value is at least <paramref name="value"/>, as one dense range: the fast subset of
        /// <c>GeConst</c> on the sort column, without allocating, for compiled plans (seminaive "new" atoms). May include
        /// tombstones. O(log runs).
        /// </summary>
        /// <param name="value">The smallest sort value wanted.</param>
        /// <returns>The range.</returns>
        public SubsetRef SortAtLeast(uint value)
        {
            RequireSorted();
            BinarySearchSortValue(value, out var start, out _);
            return SubsetRef.Dense(new OffsetRange(start, data.Count));
        }

        /// <summary>
        /// Gets the rows whose sort value is below <paramref name="value"/>, as one dense range: the fast subset of
        /// <c>LtConst</c> on the sort column, for compiled plans (seminaive "old" atoms). May include tombstones.
        /// O(log runs).
        /// </summary>
        /// <param name="value">The smallest sort value not wanted.</param>
        /// <returns>The range.</returns>
        public SubsetRef SortBelow(uint value)
        {
            RequireSorted();
            BinarySearchSortValue(value, out var start, out _);
            return SubsetRef.Dense(new OffsetRange(0, start));
        }

        /// <summary>
        /// Finds the run of rows whose sort value is <paramref name="value"/>: egglog's <c>binary_search_sort_val</c>
        /// (core-relations/src/table/mod.rs:976-1002). O(log runs).
        /// </summary>
        /// <param name="value">The sort value.</param>
        /// <param name="start">The run's first row; when there is no run, the first row with a larger value (or the
        /// physical row count).</param>
        /// <param name="end">One past the run's last row; when there is no run, equal to <paramref name="start"/>.</param>
        /// <returns><c>true</c> if some row has the value (egglog's <c>Ok</c>).</returns>
        public bool BinarySearchSortValue(uint value, out int start, out int end)
        {
            AssertRunsSorted();
            var index = new ReadOnlySpan<uint>(runValues, 0, runCount).BinarySearch(value);
            if (index >= 0)
            {
                start = runStarts[index];
                end = index + 1 < runCount ? runStarts[index + 1] : data.Count;
                return true;
            }

            var next = ~index;
            start = next < runCount ? runStarts[next] : data.Count;
            end = start;
            return false;
        }

        /// <summary>
        /// The body of the hidden-aware
        /// <see cref="ScanGenericBounded{TSink}(SubsetRef, int, int, ReadOnlySpan{Constraint}, HiddenRows, ref TSink)"/>
        /// when something is hidden: the plain scan's prologue and position rules, with
        /// <see cref="EmitIfMatchesHidden"/> per row.
        /// </summary>
        int ScanGenericBoundedHidden<TSink>(SubsetRef subset, int start, int count, ReadOnlySpan<Constraint> constraints, HiddenRows hidden, ref TSink sink)
            where TSink : struct, IRowSink
        {
            ArgumentOutOfRangeException.ThrowIfNegative(start);
            ArgumentOutOfRangeException.ThrowIfNegative(count);

            if (subset.TryGetBounds(out var bounds) == false)
                return -1;

            if (bounds.End > data.Count || bounds.Start < 0)
                ThrowSubsetOutOfRange(bounds.End);

            // Positions [from, stop) of the subset, clipped, with SubsetRef.IterBounded's resume rules.
            var size = subset.Size;
            var stop = (int)Math.Min((long)start + count, size);
            var rows = subset.Rows;
            if (rows is null)
            {
                if (start >= size)
                    return -1;
            }
            else if (start > stop)
            {
                throw new ArgumentOutOfRangeException(nameof(start), start, $"Position {start} is past the end of the chunk {stop}.");
            }

            ref var cells = ref MemoryMarshal.GetArrayDataReference(data.Data);
            var arity = Arity;
            var checkStale = staleRows > 0;
            var cursor = hidden.Cursor();
            if (rows is null)
            {
                for (int row = subset.Lo + start, last = subset.Lo + stop; row < last; row++)
                    EmitIfMatchesHidden(ref cells, arity, row, checkStale, constraints, ref cursor, ref sink);
            }
            else
            {
                foreach (var row in new ReadOnlySpan<int>(rows, subset.Lo + start, stop - start))
                    EmitIfMatchesHidden(ref cells, arity, row, checkStale, constraints, ref cursor, ref sink);
            }

            return stop < size ? stop : -1;
        }

        /// <summary>
        /// Emits the row at <paramref name="row"/> if it is live (when the table has tombstones), not hidden, and
        /// satisfies the constraints: the per-row test of a hidden-aware scan.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static void EmitIfMatchesHidden<TSink>(ref uint cells, int arity, int row, bool checkStale, ReadOnlySpan<Constraint> constraints, ref HiddenCursor hidden, ref TSink sink)
            where TSink : struct, IRowSink
        {
            ref var first = ref Unsafe.Add(ref cells, row * arity);
            if (checkStale && first == Value.StaleRep)
                return;

            if (hidden.IsHidden(row))
                return;

            var values = MemoryMarshal.CreateReadOnlySpan(ref first, arity);
            if (constraints.IsEmpty == false && Constraint.EvaluateAll(constraints, values) == false)
                return;

            sink.AddRow(row, values);
        }

        /// <summary>
        /// Emits the row at <paramref name="row"/> if it is live (when the table has tombstones) and satisfies the
        /// constraints.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static void EmitIfMatches<TSink>(ref uint cells, int arity, int row, bool checkStale, ReadOnlySpan<Constraint> constraints, ref TSink sink)
            where TSink : struct, IRowSink
        {
            ref var first = ref Unsafe.Add(ref cells, row * arity);
            if (checkStale && first == Value.StaleRep)
                return;

            var values = MemoryMarshal.CreateReadOnlySpan(ref first, arity);
            if (constraints.IsEmpty == false && Constraint.EvaluateAll(constraints, values) == false)
                return;

            sink.AddRow(row, values);
        }

        void RequireSorted()
        {
            if (sortColumn < 0)
                throw new InvalidOperationException("The table has no sort column.");
        }

        [Conditional("DEBUG")]
        void AssertRunsSorted()
        {
            for (var i = 1; i < runCount; i++)
                Debug.Assert(runValues[i - 1] < runValues[i] && runStarts[i - 1] < runStarts[i], "The sort runs are out of order.");
        }

        [DoesNotReturn]
        [MethodImpl(MethodImplOptions.NoInlining)]
        void ThrowSubsetOutOfRange(int end)
        {
            throw new ArgumentOutOfRangeException("subset", end, $"The subset extends past the table's {data.Count} physical rows.");
        }

    }

}

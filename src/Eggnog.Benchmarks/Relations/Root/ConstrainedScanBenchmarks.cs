using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

using BenchmarkDotNet.Attributes;

namespace Eggnog.Relations.Benchmarks
{

    /// <summary>
    /// Constrained scans of one <see cref="SortedWritesTable"/> through
    /// <see cref="Table.ScanGenericBounded{TSink}(SubsetRef, int, int, ReadOnlySpan{Constraint}, ref TSink)"/>, and
    /// <see cref="Table.RefineLive"/>, on tables with and without tombstones. Part of the root benchmark set, which
    /// measures the root on the engine paths colored contexts extend, so that a cost contexts add to the root shows as
    /// a regression: it exercises the scan loops (the unconstrained fast loop, the tombstone and constraint loops,
    /// dense and sparse subsets), the <see cref="TableKind"/> dispatch and the refinement filter.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The table holds <see cref="Rows"/> rows of <see cref="TableRowsWorkload"/> (<c>f(x, y) = v</c> with a
    /// timestamp, in 64 epochs), loaded once per parameter set from a fixed seed; with <see cref="Tombstones"/> a
    /// quarter of them, spread evenly, are then deleted, which leaves tombstones the scans must skip. The table is held
    /// as a <see cref="Table"/>, so every scan goes through the kind dispatch, and scans resume every
    /// <see cref="ChunkSize"/> positions, as the executor's fused scans do.
    /// </para>
    /// <list type="bullet">
    /// <item><see cref="Unconstrained"/>: every row, no constraint.</item>
    /// <item><see cref="EqConst"/>: every row, <c>v == 7</c> (about one row in 256).</item>
    /// <item><see cref="TimestampRangeFast"/>: the timestamp range [16, 32) as the executor applies it to the sort
    /// column, a fast subset (<see cref="Table.SplitFastSlow"/>) scanned without slow constraints.</item>
    /// <item><see cref="TimestampRangeScan"/>: every row, <c>ts &gt;= 16</c> and <c>ts &lt; 32</c>, evaluated per row
    /// by the scan's constraint loop (its <c>GeConst</c> and <c>LtConst</c> arms), which is how a scan applies a range
    /// it is handed as slow constraints.</item>
    /// <item><see cref="SlowConstraints"/>: every row, <c>x &gt; XRange / 2</c> and <c>v &lt;= 63</c>, evaluated per
    /// row.</item>
    /// <item><see cref="SparseSubset"/>: a sparse subset (every third row), <c>v &lt; 128</c>.</item>
    /// <item><see cref="RefineLive"/>: <see cref="Table.RefineLive"/> of every row.</item>
    /// </list>
    /// <para>
    /// Every invocation checks its own result (the emitted rows' count and the sum of their <c>y</c> cells) against a
    /// reference model computed from the generated rows, one comparison per invocation, so a broken engine fails the
    /// run instead of producing a number. The scans do not change the table, so it is built once and BenchmarkDotNet
    /// chooses the invocation count; there is no per-iteration setup to skew these microsecond-scale measurements.
    /// </para>
    /// <para>
    /// The class is not sealed because BenchmarkDotNet generates a class that derives from it.
    /// </para>
    /// </remarks>
    public class ConstrainedScanBenchmarks : IItemsPerInvocation
    {

        /// <summary>
        /// The seed every workload is generated from.
        /// </summary>
        public const int Seed = 0x5EED;

        /// <summary>
        /// The positions one scan call visits before it returns and is resumed: the executor's default morsel size.
        /// </summary>
        public const int ChunkSize = JoinRun.DefaultMorselSize;

        /// <summary>
        /// The value <see cref="EqConst"/> selects.
        /// </summary>
        const uint EqValue = 7;

        /// <summary>
        /// The first timestamp <see cref="TimestampRangeFast"/> and <see cref="TimestampRangeScan"/> select.
        /// </summary>
        const uint RangeStart = TableRowsWorkload.Epochs / 4;

        /// <summary>
        /// One past the last timestamp <see cref="TimestampRangeFast"/> and <see cref="TimestampRangeScan"/> select.
        /// </summary>
        const uint RangeEnd = TableRowsWorkload.Epochs / 2;

        /// <summary>
        /// The column the sink sums: <c>y</c>.
        /// </summary>
        const int SumColumn = 1;

        Table? table;
        SubsetRef all;
        int[]? sparseRows;
        SubsetRef sparse;
        Constraint[] eqConstraints = [];
        Constraint[] rangeConstraints = [];
        Constraint[] slowConstraints = [];
        Constraint[] sparseConstraints = [];
        readonly List<Constraint> fastScratch = new(2);
        readonly List<Constraint> slowScratch = new(2);
        (long Count, long Sum) expectedAll;
        (long Count, long Sum) expectedEq;
        (long Count, long Sum) expectedRange;
        (long Count, long Sum) expectedSlow;
        (long Count, long Sum) expectedSparse;

        /// <summary>
        /// Gets or sets the number of rows loaded (the physical rows, tombstones included).
        /// </summary>
        [Params(20_000, 200_000)]
        public int Rows { get; set; }

        /// <summary>
        /// Gets or sets whether a quarter of the rows are deleted, leaving tombstones.
        /// </summary>
        [Params(false, true)]
        public bool Tombstones { get; set; }

        /// <summary>
        /// Builds the table, the subsets and constraints, and the reference model's results.
        /// </summary>
        [GlobalSetup]
        public void GlobalSetup()
        {
            var workload = TableRowsWorkload.Generate(Rows, Seed);
            var database = new Database(ParallelHeuristics.Serial);
            var id = database.AddTable(TableRowsWorkload.CreateTable(ParallelHeuristics.Serial), name: "f");
            workload.Load(database, id, 0, workload.Count);
            if (Tombstones)
                workload.DeleteQuarter(database, id);

            table = database.GetTable(id);
            if (table.HasStaleRows != Tombstones)
                throw new InvalidOperationException($"The table {(Tombstones ? "has no" : "has")} tombstones.");

            all = SubsetRef.Dense(new OffsetRange(0, Rows));
            var sparseList = new List<int>();
            for (var i = 0; i < Rows; i += 3)
                sparseList.Add(i);

            sparseRows = sparseList.ToArray();
            sparse = SubsetRef.FromSortedRun(sparseRows, 0, sparseRows.Length);

            var xHalf = (uint)(workload.XRange / 2);
            const uint valueQuarter = TableRowsWorkload.ValueRange / 4 - 1;
            const uint valueHalf = TableRowsWorkload.ValueRange / 2;
            eqConstraints = [Constraint.EqConst(new ColumnId(2), EqValue)];
            var timestamp = new ColumnId(TableRowsWorkload.TimestampColumn);
            rangeConstraints = [Constraint.GeConst(timestamp, RangeStart), Constraint.LtConst(timestamp, RangeEnd)];
            slowConstraints = [Constraint.GtConst(new ColumnId(0), xHalf), Constraint.LeConst(new ColumnId(2), valueQuarter)];
            sparseConstraints = [Constraint.LtConst(new ColumnId(2), valueHalf)];

            expectedAll = Reference(workload, static (i, row) => true);
            expectedEq = Reference(workload, static (i, row) => row[2] == EqValue);
            expectedRange = Reference(workload, static (i, row) => row[3] >= RangeStart && row[3] < RangeEnd);
            expectedSlow = Reference(workload, (i, row) => row[0] > xHalf && row[2] <= valueQuarter);
            expectedSparse = Reference(workload, static (i, row) => i % 3 == 0 && row[2] < valueHalf);
        }

        /// <summary>
        /// Scans every row without constraints.
        /// </summary>
        /// <returns>The number of rows emitted.</returns>
        [Benchmark]
        public long Unconstrained() => Check(Scan(all, ReadOnlySpan<Constraint>.Empty), expectedAll, nameof(Unconstrained));

        /// <summary>
        /// Scans every row for <c>v == 7</c>.
        /// </summary>
        /// <returns>The number of rows emitted.</returns>
        [Benchmark]
        public long EqConst() => Check(Scan(all, eqConstraints), expectedEq, nameof(EqConst));

        /// <summary>
        /// Turns the timestamp range [16, 32) into a fast subset of the sort column and scans it.
        /// </summary>
        /// <returns>The number of rows emitted.</returns>
        [Benchmark]
        public long TimestampRangeFast()
        {
            fastScratch.Clear();
            slowScratch.Clear();
            var range = table!.SplitFastSlow(rangeConstraints, fastScratch, slowScratch);
            if (slowScratch.Count != 0)
                throw new InvalidOperationException("A timestamp range on the sort column was not turned into a fast subset.");

            var result = Scan(range.AsRef(), ReadOnlySpan<Constraint>.Empty);
            range.Dispose();
            return Check(result, expectedRange, nameof(TimestampRangeFast));
        }

        /// <summary>
        /// Scans every row for <c>ts &gt;= 16</c> and <c>ts &lt; 32</c>, evaluating the range per row.
        /// </summary>
        /// <returns>The number of rows emitted.</returns>
        [Benchmark]
        public long TimestampRangeScan() => Check(Scan(all, rangeConstraints), expectedRange, nameof(TimestampRangeScan));

        /// <summary>
        /// Scans every row for <c>x &gt; XRange / 2</c> and <c>v &lt;= 63</c>.
        /// </summary>
        /// <returns>The number of rows emitted.</returns>
        [Benchmark]
        public long SlowConstraints() => Check(Scan(all, slowConstraints), expectedSlow, nameof(SlowConstraints));

        /// <summary>
        /// Scans every third row for <c>v &lt; 128</c>.
        /// </summary>
        /// <returns>The number of rows emitted.</returns>
        [Benchmark]
        public long SparseSubset() => Check(Scan(sparse, sparseConstraints), expectedSparse, nameof(SparseSubset));

        /// <summary>
        /// Keeps the live rows of every row: <see cref="Table.RefineLive"/>, which builds a sparse subset.
        /// </summary>
        /// <returns>The number of live rows.</returns>
        [Benchmark]
        public long RefineLive()
        {
            var live = table!.RefineLive(table.All());
            var size = live.Size;
            live.Dispose();
            if (size != expectedAll.Count)
                ThrowMismatch(nameof(RefineLive), (size, 0), (expectedAll.Count, 0));

            return size;
        }

        /// <inheritdoc />
        /// <remarks>
        /// The positions of the scanned subset: every physical row, tombstones included, except for
        /// <see cref="TimestampRangeFast"/> (the rows of the range) and <see cref="SparseSubset"/> (every third row).
        /// </remarks>
        long IItemsPerInvocation.ItemsPerInvocation(string benchmarkMethod)
        {
            switch (benchmarkMethod)
            {
                case nameof(TimestampRangeFast):
                    var inRange = 0;
                    for (var i = 0; i < Rows; i++)
                    {
                        var epoch = TableRowsWorkload.EpochOf(i, Rows);
                        if (epoch >= RangeStart && epoch < RangeEnd)
                            inRange++;
                    }

                    return inRange;
                case nameof(SparseSubset):
                    return (Rows + 2) / 3;
                case nameof(Unconstrained) or nameof(EqConst) or nameof(TimestampRangeScan) or nameof(SlowConstraints) or nameof(RefineLive):
                    return Rows;
                default:
                    return 0;
            }
        }

        /// <summary>
        /// Scans <paramref name="subset"/> in chunks of <see cref="ChunkSize"/> positions, as the executor does.
        /// </summary>
        /// <param name="subset">The rows.</param>
        /// <param name="constraints">The constraints rows must satisfy.</param>
        /// <returns>The emitted rows' count and the sum of their <c>y</c> cells.</returns>
        (long Count, long Sum) Scan(SubsetRef subset, ReadOnlySpan<Constraint> constraints)
        {
            var t = table!;
            var sink = new CountingSink(SumColumn);
            var next = 0;
            do
                next = t.ScanGenericBounded(subset, next, ChunkSize, constraints, ref sink);
            while (next >= 0);

            return (sink.Count, sink.Sum);
        }

        /// <summary>
        /// Computes, over the live rows of <paramref name="workload"/>, the count and the <c>y</c> sum of the rows that
        /// <paramref name="selected"/> keeps.
        /// </summary>
        /// <param name="workload">The rows.</param>
        /// <param name="selected">Whether row <c>i</c> with cells <c>row</c> is emitted.</param>
        /// <returns>The reference result.</returns>
        (long Count, long Sum) Reference(TableRowsWorkload workload, Func<int, uint[], bool> selected)
        {
            long count = 0, sum = 0;
            var row = new uint[TableRowsWorkload.Arity];
            for (var i = 0; i < workload.Count; i++)
            {
                if (Tombstones && TableRowsWorkload.IsDeleted(i))
                    continue;

                workload.Row(i).CopyTo(row);
                if (selected(i, row))
                {
                    count++;
                    sum += row[SumColumn];
                }
            }

            return (count, sum);
        }

        /// <summary>
        /// Returns the count of <paramref name="actual"/>, or throws when it differs from <paramref name="expected"/>.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static long Check((long Count, long Sum) actual, (long Count, long Sum) expected, string benchmark)
        {
            if (actual != expected)
                ThrowMismatch(benchmark, actual, expected);

            return actual.Count;
        }

        /// <summary>
        /// Throws the exception that fails a run whose scan disagrees with the reference model.
        /// </summary>
        [DoesNotReturn]
        [MethodImpl(MethodImplOptions.NoInlining)]
        static void ThrowMismatch(string benchmark, (long Count, long Sum) actual, (long Count, long Sum) expected)
        {
            throw new InvalidOperationException(
                $"{benchmark} emitted {actual.Count} rows (sum {actual.Sum}); the reference model predicts {expected.Count} rows (sum {expected.Sum}).");
        }

    }

}

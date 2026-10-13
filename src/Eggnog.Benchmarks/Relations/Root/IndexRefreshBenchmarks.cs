using System;
using System.Collections.Generic;

using BenchmarkDotNet.Attributes;

namespace Eggnog.Relations.Benchmarks
{

    /// <summary>
    /// <see cref="Index{TIndex}.Refresh"/> of a <see cref="ColumnIndex"/> and a <see cref="TupleIndex"/>, in full and
    /// incrementally, over a table of 20,000 and 200,000 rows, serially and in parallel. Part of the root benchmark set
    /// that colored contexts are gated on (colored-contexts spec §17, M9.0): it exercises the index builds, the bulk
    /// build of a full refresh, the incremental merge of appended rows, and the parallel builds above egglog's
    /// 20,000-row threshold.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The table holds <see cref="Rows"/> rows of <see cref="TableRowsWorkload"/>, from a fixed seed, in its second
    /// generation (a first row was loaded and the table cleared). The column index is keyed by <c>x</c> (about eight
    /// rows per key), the tuple index by <c>(x, v)</c> (nearly one row per key), egglog's two index shapes.
    /// </para>
    /// <list type="bullet">
    /// <item><see cref="ColumnFull"/> and <see cref="TupleFull"/> refresh an index that has seen nothing: the table's
    /// generation differs from the index's, so the refresh clears it and rebuilds it from every row (the bulk build,
    /// or the parallel one in <see cref="RunMode.Parallel"/> above 20,000 rows).</item>
    /// <item><see cref="ColumnIncremental"/> and <see cref="TupleIncremental"/> refresh an index that reflects the
    /// first three quarters of the rows: the refresh indexes the last quarter only (in parallel above 20,000
    /// rows).</item>
    /// </list>
    /// <para>
    /// A refresh changes its index, so before every invocation (<see cref="IterationSetupAttribute"/>, one invocation
    /// per iteration) <see cref="Batch"/> fresh indexes are made (new ones, or clones of a template refreshed over the
    /// first three quarters), and the invocation refreshes all of them; BenchmarkDotNet reports the time of one
    /// refresh (<c>OperationsPerInvoke</c>), which keeps sub-millisecond refreshes measurable. After each iteration every
    /// refreshed index is checked (<see cref="IndexContentChecker"/>): it is at the table's version; it has the expected
    /// number of distinct keys and lists as many rows as the table holds; every listed row is a live row whose key cells
    /// equal the key it is listed under; and the order-independent checksum of its (row, key) entries equals the
    /// table's, so an index that lists a wrong row under the right key fails too. A broken engine therefore fails the
    /// run instead of producing a number.
    /// </para>
    /// <para>
    /// The class is not sealed because BenchmarkDotNet generates a class that derives from it.
    /// </para>
    /// </remarks>
    [InvocationCount(1)]
    public class IndexRefreshBenchmarks : IItemsPerInvocation
    {

        /// <summary>
        /// The seed every workload is generated from.
        /// </summary>
        public const int Seed = 0x5EED;

        /// <summary>
        /// The number of indexes one invocation refreshes; BenchmarkDotNet divides the time by it.
        /// </summary>
        public const int Batch = 16;

        /// <summary>
        /// The column index's key: <c>x</c>.
        /// </summary>
        static readonly ColumnId[] ColumnKey = [new ColumnId(0)];

        /// <summary>
        /// The tuple index's key: <c>(x, v)</c>.
        /// </summary>
        static readonly ColumnId[] TupleKey = [new ColumnId(0), new ColumnId(2)];

        readonly Index<ColumnIndex>?[] columnIndexes = new Index<ColumnIndex>?[Batch];
        readonly Index<TupleIndex>?[] tupleIndexes = new Index<TupleIndex>?[Batch];
        SortedWritesTable? table;
        Index<ColumnIndex>? columnTemplate;
        Index<TupleIndex>? tupleTemplate;
        ParallelHeuristics? parallelism;
        int expectedColumnKeys;
        int expectedTupleKeys;
        ulong expectedColumnChecksum;
        ulong expectedTupleChecksum;
        bool ran;

        /// <summary>
        /// Gets or sets the number of rows the table holds.
        /// </summary>
        [Params(20_000, 200_000)]
        public int Rows { get; set; }

        /// <summary>
        /// Gets or sets whether the indexes refresh on the serial or the parallel paths.
        /// </summary>
        [Params(RunMode.Serial, RunMode.Parallel)]
        public RunMode Mode { get; set; }

        /// <summary>
        /// Builds the table in its second generation, the incremental templates over its first three quarters, and the
        /// expected key counts and checksums.
        /// </summary>
        /// <exception cref="InvalidOperationException">The table does not hold the workload's rows.</exception>
        [GlobalSetup]
        public void GlobalSetup()
        {
            parallelism = RootParallelism.For(Mode);
            var workload = TableRowsWorkload.Generate(Rows, Seed);
            var database = new Database(ParallelHeuristics.Serial);
            var id = database.AddTable(TableRowsWorkload.CreateTable(ParallelHeuristics.Serial), name: "f");

            // One row, cleared, moves the table to its second generation: an index that has seen nothing then refreshes
            // in full, as after a compaction, instead of indexing every row as appended ones.
            workload.Load(database, id, 0, 1);
            database.ClearTable(id);

            var baseRows = Rows - Rows / 4;
            workload.Load(database, id, 0, baseRows);
            table = (SortedWritesTable)database.GetTable(id);
            columnTemplate = new Index<ColumnIndex>(ColumnKey, ColumnIndex.Create(ColumnKey.Length, parallelism));
            columnTemplate.Refresh(table);
            tupleTemplate = new Index<TupleIndex>(TupleKey, TupleIndex.Create(TupleKey.Length, parallelism));
            tupleTemplate.Refresh(table);
            workload.Load(database, id, baseRows, Rows);
            if (table.Version != new TableVersion(1, Rows) || columnTemplate.UpdatedTo != new TableVersion(1, baseRows))
                throw new InvalidOperationException($"The table is at {table.Version} and the templates at {columnTemplate.UpdatedTo}.");

            // The reference model: row i of the table is row i of the workload, so the expected (row, key) entries
            // come from the workload.
            var data = table.Rows;
            if (data.Count != Rows || table.Count != Rows)
                throw new InvalidOperationException($"The table holds {data.Count} rows ({table.Count} live); the workload has {Rows}.");

            var xs = new HashSet<uint>();
            var pairs = new HashSet<(uint, uint)>();
            var columnChecksum = 0UL;
            var tupleChecksum = 0UL;
            for (var i = 0; i < Rows; i++)
            {
                var row = workload.Row(i);
                if (data.GetRow(i).SequenceEqual(row) == false)
                    throw new InvalidOperationException($"Row {i} of the table is not row {i} of the workload.");

                xs.Add(row[0]);
                pairs.Add((row[0], row[2]));
                columnChecksum = unchecked(columnChecksum + IndexContentChecker.Entry(i, [row[0]]));
                tupleChecksum = unchecked(tupleChecksum + IndexContentChecker.Entry(i, [row[0], row[2]]));
            }

            expectedColumnKeys = xs.Count;
            expectedTupleKeys = pairs.Count;
            expectedColumnChecksum = columnChecksum;
            expectedTupleChecksum = tupleChecksum;
        }

        /// <summary>
        /// Releases the templates.
        /// </summary>
        [GlobalCleanup]
        public void GlobalCleanup()
        {
            columnTemplate?.Data.Dispose();
            tupleTemplate?.Data.Dispose();
            columnTemplate = null;
            tupleTemplate = null;
            table = null;
        }

        /// <summary>
        /// Makes <see cref="Batch"/> column indexes that have seen nothing.
        /// </summary>
        [IterationSetup(Target = nameof(ColumnFull))]
        public void IterationSetupColumnFull()
        {
            for (var i = 0; i < Batch; i++)
                columnIndexes[i] = new Index<ColumnIndex>(ColumnKey, ColumnIndex.Create(ColumnKey.Length, parallelism!));
        }

        /// <summary>
        /// Makes <see cref="Batch"/> clones of the column template.
        /// </summary>
        [IterationSetup(Target = nameof(ColumnIncremental))]
        public void IterationSetupColumnIncremental()
        {
            for (var i = 0; i < Batch; i++)
                columnIndexes[i] = columnTemplate!.Clone();
        }

        /// <summary>
        /// Makes <see cref="Batch"/> tuple indexes that have seen nothing.
        /// </summary>
        [IterationSetup(Target = nameof(TupleFull))]
        public void IterationSetupTupleFull()
        {
            for (var i = 0; i < Batch; i++)
                tupleIndexes[i] = new Index<TupleIndex>(TupleKey, TupleIndex.Create(TupleKey.Length, parallelism!));
        }

        /// <summary>
        /// Makes <see cref="Batch"/> clones of the tuple template.
        /// </summary>
        [IterationSetup(Target = nameof(TupleIncremental))]
        public void IterationSetupTupleIncremental()
        {
            for (var i = 0; i < Batch; i++)
                tupleIndexes[i] = tupleTemplate!.Clone();
        }

        /// <summary>
        /// Checks and releases the column indexes. An iteration that did not run the benchmark is not checked.
        /// </summary>
        /// <exception cref="InvalidOperationException">An index is not at the table's version or does not list exactly the
        /// table's rows, each under its own key.</exception>
        [IterationCleanup(Targets = new[] { nameof(ColumnFull), nameof(ColumnIncremental) })]
        public void IterationCleanupColumns()
        {
            var check = ran;
            ran = false;
            string? failure = null;
            for (var i = 0; i < Batch; i++)
            {
                var index = columnIndexes[i]!;
                columnIndexes[i] = null;
                if (check && failure is null)
                    failure = Verify(index.UpdatedTo, index.Count, IndexContentChecker.Check(index.Data, table!, ColumnKey), expectedColumnKeys, expectedColumnChecksum);

                index.Data.Dispose();
            }

            if (failure is not null)
                throw new InvalidOperationException("Column index: " + failure);
        }

        /// <summary>
        /// Checks and releases the tuple indexes. An iteration that did not run the benchmark is not checked.
        /// </summary>
        /// <exception cref="InvalidOperationException">An index is not at the table's version or does not list exactly the
        /// table's rows, each under its own key.</exception>
        [IterationCleanup(Targets = new[] { nameof(TupleFull), nameof(TupleIncremental) })]
        public void IterationCleanupTuples()
        {
            var check = ran;
            ran = false;
            string? failure = null;
            for (var i = 0; i < Batch; i++)
            {
                var index = tupleIndexes[i]!;
                tupleIndexes[i] = null;
                if (check && failure is null)
                    failure = Verify(index.UpdatedTo, index.Count, IndexContentChecker.Check(index.Data, table!, TupleKey), expectedTupleKeys, expectedTupleChecksum);

                index.Data.Dispose();
            }

            if (failure is not null)
                throw new InvalidOperationException("Tuple index: " + failure);
        }

        /// <summary>
        /// Refreshes column indexes that have seen nothing: a full rebuild over every row.
        /// </summary>
        [Benchmark(OperationsPerInvoke = Batch)]
        public void ColumnFull() => RefreshColumns();

        /// <summary>
        /// Refreshes column indexes over the first three quarters of the rows: the last quarter is indexed.
        /// </summary>
        [Benchmark(OperationsPerInvoke = Batch)]
        public void ColumnIncremental() => RefreshColumns();

        /// <summary>
        /// Refreshes tuple indexes that have seen nothing: a full rebuild over every row.
        /// </summary>
        [Benchmark(OperationsPerInvoke = Batch)]
        public void TupleFull() => RefreshTuples();

        /// <summary>
        /// Refreshes tuple indexes over the first three quarters of the rows: the last quarter is indexed.
        /// </summary>
        [Benchmark(OperationsPerInvoke = Batch)]
        public void TupleIncremental() => RefreshTuples();

        /// <inheritdoc />
        /// <remarks>
        /// The rows one refresh indexes (BenchmarkDotNet reports per refresh, not per invocation of
        /// <see cref="Batch"/>): every row for a full refresh, the last quarter for an incremental one.
        /// </remarks>
        long IItemsPerInvocation.ItemsPerInvocation(string benchmarkMethod) => benchmarkMethod switch
        {
            nameof(ColumnFull) or nameof(TupleFull) => Rows,
            nameof(ColumnIncremental) or nameof(TupleIncremental) => Rows / 4,
            _ => 0,
        };

        /// <summary>
        /// Refreshes every column index of the batch.
        /// </summary>
        void RefreshColumns()
        {
            var t = table!;
            foreach (var index in columnIndexes)
                index!.Refresh(t);

            ran = true;
        }

        /// <summary>
        /// Refreshes every tuple index of the batch.
        /// </summary>
        void RefreshTuples()
        {
            var t = table!;
            foreach (var index in tupleIndexes)
                index!.Refresh(t);

            ran = true;
        }

        /// <summary>
        /// Checks one refreshed index against the table: its version, its key count, the rows it lists and their
        /// checksum.
        /// </summary>
        /// <param name="updatedTo">The version the index was refreshed to.</param>
        /// <param name="count">The index's key count.</param>
        /// <param name="checker">The result of visiting the index.</param>
        /// <param name="expectedKeys">The number of distinct keys in the table.</param>
        /// <param name="expectedChecksum">The checksum of the table's (row, key) entries.</param>
        /// <returns>A description of what is wrong, or <c>null</c>.</returns>
        string? Verify(TableVersion updatedTo, int count, IndexContentChecker checker, int expectedKeys, ulong expectedChecksum)
        {
            var version = table!.Version;
            if (updatedTo != version)
                return $"refreshed to {updatedTo}, the table is at {version}.";

            if (count != expectedKeys || checker.Keys != expectedKeys || checker.Rows != Rows)
                return $"{count} keys ({checker.Keys} visited) listing {checker.Rows} rows; expected {expectedKeys} keys listing {Rows} rows.";

            if (checker.Misplaced != 0)
                return $"{checker.Misplaced} listed rows are deleted, out of range, or listed under a key their cells do not hold.";

            if (checker.Checksum != expectedChecksum)
                return $"the listed (row, key) entries have checksum {checker.Checksum:X16}; the table's have {expectedChecksum:X16}.";

            return null;
        }

    }

}

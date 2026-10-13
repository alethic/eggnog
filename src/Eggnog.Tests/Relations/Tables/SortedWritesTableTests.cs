using System;
using System.Collections.Generic;
using System.Linq;

using Xunit;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// Tests for <see cref="SortedWritesTable"/>: ports of egglog's core-relations/src/table/tests.rs and unit tests of
    /// the table's contract (staging, merging, lookups, scans, fast subsets, compaction, clearing, cloning).
    /// </summary>
    public class SortedWritesTableTests
    {

        static uint[] R(params uint[] cells) => cells;

        static ColumnId C(int column) => new(column);

        /// <summary>
        /// Port of <c>empty_key</c>: a table without key columns holds one row, replaced on every insert.
        /// </summary>
        [Fact]
        public void EmptyKey()
        {
            var state = TableTestUtil.EmptyState();
            var table = TableTestUtil.FillTable([R(1, 2), R(2, 3)], 0, null, ClosureMerge.TakeNew);

            var row = table.GetRow(ReadOnlySpan<uint>.Empty);
            Assert.NotNull(row);
            Assert.Equal(R(2, 3), row.Value.Values);

            TableTestUtil.StageRemove(table, R());
            table.Merge(state);
            Assert.Null(table.GetRow(ReadOnlySpan<uint>.Empty));

            TableTestUtil.Stage(table, R(1, 2));
            table.Merge(state);
            row = table.GetRow(ReadOnlySpan<uint>.Empty);
            Assert.NotNull(row);
            Assert.Equal(R(1, 2), row.Value.Values);
        }

        /// <summary>
        /// Port of <c>insert_scan</c>: a superseded row is not scanned, and projections follow the column order.
        /// </summary>
        [Fact]
        public void InsertScan()
        {
            var table = TableTestUtil.FillTable([R(0, 1, 2), R(1, 2, 3), R(2, 3, 4), R(3, 4, 5), R(2, 3, 6)], 2, null, ClosureMerge.TakeNew);

            var smaller = table.RefineOne(table.All(), Constraint.GtConst(C(2), 4));
            var rows = TableTestUtil.DumpSubset(table, smaller.AsRef());
            Assert.Equal(2, rows.Count);
            Assert.Equal(3, rows[0].Id);
            Assert.Equal(R(3, 4, 5), rows[0].Row);
            Assert.Equal(4, rows[1].Id);
            Assert.Equal(R(2, 3, 6), rows[1].Row);

            var buffer = new TaggedRowBuffer(2);
            Table wrapped = table;
            Assert.Equal(-1, wrapped.ScanProject(smaller.AsRef(), [C(1), C(0)], 0, int.MaxValue, ReadOnlySpan<Constraint>.Empty, buffer));
            var projection = TableTestUtil.DumpBuffer(buffer);
            Assert.Equal(2, projection.Count);
            Assert.Equal(3, projection[0].Id);
            Assert.Equal(R(4, 3), projection[0].Row);
            Assert.Equal(4, projection[1].Id);
            Assert.Equal(R(3, 2), projection[1].Row);
            smaller.Dispose();
        }

        /// <summary>
        /// Port of <c>insert_scan_sorted</c>: refinement on sorted and unsorted columns skips the superseded row.
        /// </summary>
        [Fact]
        public void InsertScanSorted()
        {
            var table = TableTestUtil.FillTable([R(0, 1, 2), R(1, 2, 3), R(2, 3, 4), R(3, 4, 5), R(2, 3, 6)], 2, C(2), ClosureMerge.TakeNew);

            var smaller = table.RefineOne(table.All(), Constraint.LtConst(C(1), 4));
            var rows = TableTestUtil.DumpSubset(table, smaller.AsRef());
            Assert.Equal(new[] { 0, 1, 4 }, rows.Select(x => x.Id));
            Assert.Equal(R(0, 1, 2), rows[0].Row);
            Assert.Equal(R(1, 2, 3), rows[1].Row);
            Assert.Equal(R(2, 3, 6), rows[2].Row);

            var sortedSmaller = table.RefineOne(table.All(), Constraint.LtConst(C(2), 5));
            rows = TableTestUtil.DumpSubset(table, sortedSmaller.AsRef());
            Assert.Equal(new[] { 0, 1 }, rows.Select(x => x.Id));
            Assert.Equal(R(0, 1, 2), rows[0].Row);
            Assert.Equal(R(1, 2, 3), rows[1].Row);
            smaller.Dispose();
            sortedSmaller.Dispose();
        }

        /// <summary>
        /// Port of <c>shard_math</c>: shard counts round up to a power of two, key hashes spread over every shard, and
        /// small hashes map to shard 0.
        /// </summary>
        [Fact]
        public void ShardMath()
        {
            var table = new ShardedHashTable<TableEntry>(14);
            Assert.Equal(16, table.ShardCount);

            var random = new Random(1234);
            var histogram = new Dictionary<int, int>();
            for (var i = 0; i < 100_000; i++)
            {
                var row = R((uint)random.Next(), (uint)random.Next(), (uint)random.Next());
                var shard = table.ShardData.ShardOf(TableKeyHash.Hash(row, 2));
                histogram[shard] = histogram.GetValueOrDefault(shard) + 1;
            }

            Assert.Equal(16, histogram.Count);
            Assert.All(histogram.Values, count => Assert.True(count > 100));

            for (var x = 0UL; x < 100_000; x++)
                Assert.Equal(0, table.ShardData.ShardOf(x));
        }

        [Fact]
        public void KeyHashMatchesTheFxHashOfEachKeyCell()
        {
            Assert.Equal(0UL, TableKeyHash.Hash(ReadOnlySpan<uint>.Empty, 0));
            Assert.Equal(FxHasher.Hash(7), TableKeyHash.Hash(R(7, 99), 1));
            Assert.Equal(FxHasher.Hash(7, 8), TableKeyHash.Hash(R(7, 8, 99), 2));
            Assert.Equal(FxHasher.Hash(7, 8, 9), TableKeyHash.Hash(R(7, 8, 9), 3));
            Assert.Equal(FxHasher.Hash(R(1, 2, 3, 4, 5)), TableKeyHash.Hash(R(1, 2, 3, 4, 5, 6), 5));
            Assert.Equal(FxHasher.Hash(R(1, 2, 3, 4, 5)), TableKeyHash.Hash(R(0, 1, 2, 3, 4, 5, 6), 1, 5));
            Assert.Throws<ArgumentException>(() => TableKeyHash.Hash(R(1), 2));
        }

        [Fact]
        public void ConstructorValidatesTheSchema()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new SortedWritesTable(0, 0, null, ReadOnlySpan<ColumnId>.Empty, ClosureMerge.TakeNew));
            Assert.Throws<ArgumentOutOfRangeException>(() => new SortedWritesTable(3, 2, null, ReadOnlySpan<ColumnId>.Empty, ClosureMerge.TakeNew));
            Assert.Throws<ArgumentOutOfRangeException>(() => new SortedWritesTable(1, 2, C(2), ReadOnlySpan<ColumnId>.Empty, ClosureMerge.TakeNew));
            Assert.Throws<ArgumentOutOfRangeException>(() => new SortedWritesTable(1, 2, null, [C(5)], ClosureMerge.TakeNew));
            Assert.Throws<ArgumentNullException>(() => new SortedWritesTable(1, 2, null, ReadOnlySpan<ColumnId>.Empty, null!));

            var table = new SortedWritesTable(2, 4, C(3), [C(0), C(2)], ClosureMerge.TakeNew, ParallelHeuristics.Serial);
            Assert.Equal(TableKind.SortedWrites, table.Kind);
            Assert.Equal(4, table.Arity);
            Assert.Equal(2, table.Spec.KeyCount);
            Assert.Equal(2, table.Spec.ValueCount);
            Assert.True(table.Spec.AllowsDelete);
            Assert.False(table.Spec.IsUncacheable(C(1)));
            Assert.Equal(3, table.SortColumn);
            Assert.Equal(new[] { C(0), C(2) }, table.RebuildColumns.ToArray());
            Assert.Equal(new TableVersion(0, 0), table.Version);
            Assert.True(table.IsEmpty);
        }

        [Fact]
        public void StagedWritesAreInvisibleUntilFlushedAndMerged()
        {
            var state = TableTestUtil.EmptyState();
            var table = new SortedWritesTable(1, 2, null, ReadOnlySpan<ColumnId>.Empty, ClosureMerge.TakeNew, ParallelHeuristics.Serial);
            var buffer = table.NewBuffer();
            buffer.StageInsert(R(1, 10));

            Assert.Equal(new TableChange(false, false), table.Merge(state));
            Assert.Equal(-1, table.FindRow(1));

            buffer.Flush();
            Assert.Equal(-1, table.FindRow(1));
            Assert.Equal(new TableChange(true, false), table.Merge(state));
            Assert.Equal(0, table.FindRow(1));
            Assert.Equal(1, table.Count);
        }

        [Fact]
        public void MergeFoldsRowsOfOneKeyInStagingOrder()
        {
            var state = TableTestUtil.EmptyState();
            var seen = new List<(uint Current, uint Incoming)>();
            var merge = new ClosureMerge((current, incoming) =>
            {
                seen.Add((current[1], incoming[1]));
                return R(current[0], current[1] * 10 + incoming[1]);
            });
            var table = new SortedWritesTable(1, 2, null, ReadOnlySpan<ColumnId>.Empty, merge, ParallelHeuristics.Serial);

            TableTestUtil.Stage(table, R(5, 1), R(5, 2), R(5, 3));
            Assert.True(table.Merge(state).Added);
            Assert.Equal(new[] { (1u, 2u), (12u, 3u) }, seen);
            Assert.True(table.TryGetColumn(5, 1, out var value));
            Assert.Equal(123u, value);
            Assert.Equal(1, table.Count);
            Assert.Equal(2, table.StaleCount);
        }

        [Fact]
        public void MergeReturningFalseWritesNothing()
        {
            var state = TableTestUtil.EmptyState();
            var table = new SortedWritesTable(1, 3, C(2), ReadOnlySpan<ColumnId>.Empty, new MinMerge(C(1)), ParallelHeuristics.Serial);
            TableTestUtil.Stage(table, R(1, 5, 1));
            table.Merge(state);
            var version = table.Version;

            TableTestUtil.Stage(table, R(1, 7, 2), R(1, 5, 2));
            Assert.Equal(new TableChange(false, false), table.Merge(state));
            Assert.Equal(version, table.Version);
            Assert.Equal(R(1, 5, 1), table.GetRow(R(1))!.Value.Values);

            TableTestUtil.Stage(table, R(1, 3, 3));
            Assert.Equal(new TableChange(true, false), table.Merge(state));
            Assert.Equal(R(1, 3, 3), table.GetRow(R(1))!.Value.Values);
        }

        [Fact]
        public void RemoveAndInsertOfOneKeyInOneBatchReplacesTheRowWithoutMerging()
        {
            var state = TableTestUtil.EmptyState();
            var calls = 0;
            var merge = new ClosureMerge((current, incoming) =>
            {
                calls++;
                return null;
            });
            var table = new SortedWritesTable(1, 2, null, ReadOnlySpan<ColumnId>.Empty, merge, ParallelHeuristics.Serial);
            TableTestUtil.Stage(table, R(1, 10));
            table.Merge(state);

            // Staged insert first and removal second: removals still apply first.
            var buffer = table.NewBuffer();
            buffer.StageInsert(R(1, 20));
            buffer.StageRemove(R(1));
            buffer.Flush();
            Assert.Equal(new TableChange(true, true), table.Merge(state));
            Assert.Equal(0, calls);
            Assert.Equal(R(1, 20), table.GetRow(R(1))!.Value.Values);
        }

        [Fact]
        public void RemovingAMissingKeyIsANoOp()
        {
            var state = TableTestUtil.EmptyState();
            var table = TableTestUtil.FillTable([R(1, 10)], 1, null, ClosureMerge.TakeNew);
            TableTestUtil.StageRemove(table, R(2), R(3));
            Assert.Equal(new TableChange(false, false), table.Merge(state));
            Assert.Equal(1, table.Count);
            Assert.False(table.HasStaleRows);
        }

        [Fact]
        public void PointLookupsCoverEveryKeyWidth()
        {
            var state = TableTestUtil.EmptyState();
            for (var keyCount = 0; keyCount <= 5; keyCount++)
            {
                var arity = keyCount + 2;
                var table = new SortedWritesTable(keyCount, arity, null, ReadOnlySpan<ColumnId>.Empty, ClosureMerge.TakeNew, new ParallelHeuristics(4));
                var rows = new List<uint[]>();
                for (uint i = 0; i < (keyCount == 0 ? 1u : 50u); i++)
                {
                    var row = new uint[arity];
                    for (var c = 0; c < keyCount; c++)
                        row[c] = i * 7 + (uint)c;

                    row[keyCount] = i + 100;
                    row[keyCount + 1] = i + 200;
                    rows.Add(row);
                }

                TableTestUtil.Stage(table, rows.ToArray());
                table.Merge(state);
                Assert.Equal(rows.Count, table.Count);

                foreach (var row in rows)
                {
                    var key = row[..keyCount];
                    var id = table.FindRow(key);
                    Assert.True(id >= 0);
                    Assert.Equal(row, table.Rows.GetRow(id).ToArray());
                    Assert.Equal(id, table.FindRow(row, 0));
                    Assert.True(table.TryGetRowColumn(key, C(keyCount + 1), out var value));
                    Assert.Equal(row[keyCount + 1], value);
                    Assert.True(table.TryGetColumn(row, 0, keyCount, out value));
                    Assert.Equal(row[keyCount], value);

                    var destination = new uint[arity];
                    Assert.True(table.TryGetRow(key, destination, out var rowId));
                    Assert.Equal(id, rowId);
                    Assert.Equal(row, destination);

                    switch (keyCount)
                    {
                        case 1:
                            Assert.Equal(id, table.FindRow(key[0]));
                            Assert.True(table.TryGetColumn(key[0], 1, out value));
                            Assert.Equal(row[1], value);
                            break;
                        case 2:
                            Assert.Equal(id, table.FindRow(key[0], key[1]));
                            Assert.True(table.TryGetColumn(key[0], key[1], 2, out value));
                            Assert.Equal(row[2], value);
                            break;
                        case 3:
                            Assert.Equal(id, table.FindRow(key[0], key[1], key[2]));
                            Assert.True(table.TryGetColumn(key[0], key[1], key[2], 3, out value));
                            Assert.Equal(row[3], value);
                            break;
                    }
                }

                if (keyCount > 0)
                {
                    var missing = Enumerable.Repeat(999_999u, keyCount).ToArray();
                    Assert.Equal(-1, table.FindRow(missing));
                    Assert.False(table.TryGetRowColumn(missing, C(0), out _));
                    Assert.Null(table.GetRow(missing));
                }
            }
        }

        [Fact]
        public void LookupsRejectMismatchedKeysAndColumns()
        {
            var table = TableTestUtil.FillTable([R(1, 2, 3)], 2, null, ClosureMerge.TakeNew);
            Assert.Throws<InvalidOperationException>(() => table.FindRow(1));
            Assert.Throws<InvalidOperationException>(() => table.FindRow(1, 2, 3));
            Assert.Throws<ArgumentException>(() => table.FindRow(R(1)));
            Assert.Throws<ArgumentOutOfRangeException>(() => table.FindRow(R(1), 0));
            Assert.Throws<ArgumentOutOfRangeException>(() => table.TryGetColumn(1, 2, 3, out _));
            Assert.Throws<ArgumentException>(() => table.TryGetRow(R(1, 2), new uint[2], out _));
            Assert.True(table.TryGetColumn(1, 2, 2, out var value));
            Assert.Equal(3u, value);
        }

        [Fact]
        public void FastSubsetsFollowTheSortRuns()
        {
            var state = TableTestUtil.EmptyState();
            var table = new SortedWritesTable(1, 2, C(1), ReadOnlySpan<ColumnId>.Empty, ClosureMerge.TakeNew, ParallelHeuristics.Serial);

            // Sort values 2, 2, 4, 4, 4, 7 at rows 0..5.
            uint key = 0;
            foreach (var ts in new uint[] { 2, 2, 4, 4, 4, 7 })
            {
                TableTestUtil.Stage(table, R(key++, ts));
                table.Merge(state);
            }

            Assert.Equal(3, table.SortRunCount);
            Assert.True(table.BinarySearchSortValue(4, out var start, out var end));
            Assert.Equal((2, 5), (start, end));
            Assert.False(table.BinarySearchSortValue(3, out start, out end));
            Assert.Equal((2, 2), (start, end));
            Assert.False(table.BinarySearchSortValue(9, out start, out end));
            Assert.Equal((6, 6), (start, end));
            Assert.False(table.BinarySearchSortValue(1, out start, out _));
            Assert.Equal(0, start);

            AssertFast(table, Constraint.EqConst(C(1), 4), 2, 5);
            AssertFast(table, Constraint.EqConst(C(1), 3), 0, 0);
            AssertFast(table, Constraint.LtConst(C(1), 4), 0, 2);
            AssertFast(table, Constraint.LtConst(C(1), 3), 0, 2);
            AssertFast(table, Constraint.LeConst(C(1), 4), 0, 5);
            AssertFast(table, Constraint.LeConst(C(1), 5), 0, 5);
            AssertFast(table, Constraint.GtConst(C(1), 4), 5, 6);
            AssertFast(table, Constraint.GtConst(C(1), 3), 2, 6);
            AssertFast(table, Constraint.GeConst(C(1), 4), 2, 6);
            AssertFast(table, Constraint.GeConst(C(1), 5), 5, 6);
            AssertFast(table, Constraint.GeConst(C(1), 0), 0, 6);
            AssertFast(table, Constraint.GtConst(C(1), 7), 6, 6);

            Assert.False(table.TryFastSubset(Constraint.EqConst(C(0), 1), out _));
            Assert.False(table.TryFastSubset(Constraint.Eq(C(1), C(1)), out _));

            Assert.Equal(SubsetRef.Dense(new OffsetRange(2, 6)).ToString(), table.SortAtLeast(4).ToString());
            Assert.Equal(SubsetRef.Dense(new OffsetRange(0, 2)).ToString(), table.SortBelow(4).ToString());

            var fast = new List<Constraint>();
            var slow = new List<Constraint>();
            var split = table.SplitFastSlow([Constraint.GeConst(C(1), 4), Constraint.EqConst(C(0), 3), Constraint.LtConst(C(1), 7)], fast, slow);
            Assert.Equal(Subset.Dense(2, 5), split);
            Assert.Equal(new[] { Constraint.GeConst(C(1), 4), Constraint.LtConst(C(1), 7) }, fast);
            Assert.Equal(new[] { Constraint.EqConst(C(0), 3) }, slow);

            var unsorted = TableTestUtil.FillTable([R(1, 2)], 1, null, ClosureMerge.TakeNew);
            Assert.False(unsorted.TryFastSubset(Constraint.GeConst(C(1), 0), out _));
            Assert.Throws<InvalidOperationException>(() => unsorted.SortAtLeast(0));
        }

        static void AssertFast(SortedWritesTable table, Constraint constraint, int start, int end)
        {
            Assert.True(table.TryFastSubset(constraint, out var subset));
            var expected = start == end ? 0 : end - start;
            Assert.Equal(expected, subset.Size);
            if (expected > 0)
                Assert.Equal(Subset.Dense(start, end), subset);
        }

        [Fact]
        public void SortOrderViolationsThrowBeforeWriting()
        {
            var state = TableTestUtil.EmptyState();
            var table = new SortedWritesTable(1, 2, C(1), ReadOnlySpan<ColumnId>.Empty, ClosureMerge.TakeNew, ParallelHeuristics.Serial);
            TableTestUtil.Stage(table, R(1, 5));
            table.Merge(state);

            TableTestUtil.Stage(table, R(2, 4));
            Assert.Throws<InvalidOperationException>(() => table.Merge(state));
            Assert.Equal(1, table.Rows.Count);
            Assert.Equal(-1, table.FindRow(2));

            // An existing key whose merge changes the row is checked too.
            TableTestUtil.Stage(table, R(1, 3));
            Assert.Throws<InvalidOperationException>(() => table.Merge(state));
            Assert.Equal(R(1, 5), table.GetRow(R(1))!.Value.Values);
        }

        [Fact]
        public void BoundedScansResumeByPosition()
        {
            var table = TableTestUtil.FillTable(Enumerable.Range(0, 10).Select(i => R((uint)i, (uint)i * 10)), 1, null, ClosureMerge.TakeNew);
            var state = TableTestUtil.EmptyState();
            TableTestUtil.StageRemove(table, R(3), R(4));
            table.Merge(state);

            // Dense: positions count rows of the subset, tombstones included.
            var collector = new RowCollector();
            var next = table.ScanGenericBounded(SubsetRef.Dense(new OffsetRange(2, 9)), 0, 3, ReadOnlySpan<Constraint>.Empty, ref collector);
            Assert.Equal(3, next);
            Assert.Equal(new[] { 2 }, collector.Rows.Select(x => x.Id));
            next = table.ScanGenericBounded(SubsetRef.Dense(new OffsetRange(2, 9)), next, 3, ReadOnlySpan<Constraint>.Empty, ref collector);
            Assert.Equal(6, next);
            next = table.ScanGenericBounded(SubsetRef.Dense(new OffsetRange(2, 9)), next, 3, ReadOnlySpan<Constraint>.Empty, ref collector);
            Assert.Equal(-1, next);
            Assert.Equal(new[] { 2, 5, 6, 7, 8 }, collector.Rows.Select(x => x.Id));
            Assert.Equal(-1, table.ScanGenericBounded(SubsetRef.Dense(new OffsetRange(2, 9)), 7, 3, ReadOnlySpan<Constraint>.Empty, ref collector));

            // Sparse.
            var sparse = Subset.Empty;
            foreach (var row in new[] { 1, 3, 5, 9 })
                sparse.AddRowSorted(row);

            collector = new RowCollector();
            next = table.ScanGenericBounded(sparse.AsRef(), 0, 2, ReadOnlySpan<Constraint>.Empty, ref collector);
            Assert.Equal(2, next);
            next = table.ScanGenericBounded(sparse.AsRef(), next, 2, ReadOnlySpan<Constraint>.Empty, ref collector);
            Assert.Equal(-1, next);
            Assert.Equal(new[] { 1, 5, 9 }, collector.Rows.Select(x => x.Id));
            sparse.Dispose();

            // Empty and out-of-range subsets.
            Assert.Equal(-1, table.ScanGenericBounded(SubsetRef.Dense(new OffsetRange(4, 4)), 0, 5, ReadOnlySpan<Constraint>.Empty, ref collector));
            Assert.Throws<ArgumentOutOfRangeException>(() =>
            {
                var c = new RowCollector();
                table.ScanGenericBounded(SubsetRef.Dense(new OffsetRange(0, 11)), 0, 5, ReadOnlySpan<Constraint>.Empty, ref c);
            });
        }

        [Fact]
        public void ScanProjectHandlesEveryProjectionWidth()
        {
            var table = TableTestUtil.FillTable([R(1, 2, 3, 4, 5), R(6, 7, 8, 9, 10)], 1, null, ClosureMerge.TakeNew);
            var all = table.All().AsRef();

            AssertProjection(table, all, [], []);
            AssertProjection(table, all, [C(3)], [R(4), R(9)]);
            AssertProjection(table, all, [C(4), C(0)], [R(5, 1), R(10, 6)]);
            AssertProjection(table, all, [C(2), C(2), C(1)], [R(3, 3, 2), R(8, 8, 7)]);
            AssertProjection(table, all, [C(4), C(3), C(2), C(1), C(0)], [R(5, 4, 3, 2, 1), R(10, 9, 8, 7, 6)]);

            var output = new TaggedRowBuffer(1);
            Assert.Equal(-1, table.ScanProject(all, [C(1)], 0, int.MaxValue, [Constraint.GtConst(C(0), 3)], output));
            var projected = TableTestUtil.DumpBuffer(output);
            Assert.Single(projected);
            Assert.Equal(1, projected[0].Id);
            Assert.Equal(R(7), projected[0].Row);

            Assert.Throws<ArgumentOutOfRangeException>(() => table.ScanProject(all, [C(5)], 0, 1, ReadOnlySpan<Constraint>.Empty, new TaggedRowBuffer(1)));

            var collector = new RowCollector();
            table.ForEachColumn(all, C(2), ref collector);
            Assert.Equal(new[] { R(3), R(8) }, collector.Rows.Select(x => x.Row));
        }

        static void AssertProjection(SortedWritesTable table, SubsetRef subset, ColumnId[] columns, uint[][] expected)
        {
            var collector = new RowCollector();
            Assert.Equal(-1, table.ScanProject(subset, columns, 0, int.MaxValue, ReadOnlySpan<Constraint>.Empty, ref collector));
            Assert.Equal(expected, collector.Rows.Select(x => x.Row).ToArray());
        }

        [Fact]
        public void UpdatesSinceIsTheAppendedSuffix()
        {
            var state = TableTestUtil.EmptyState();
            var table = TableTestUtil.FillTable([R(1, 1), R(2, 2)], 1, null, ClosureMerge.TakeNew);
            var version = table.Version;
            TableTestUtil.Stage(table, R(3, 3), R(1, 9));
            table.Merge(state);
            Assert.Equal(version.Major, table.Version.Major);
            Assert.Equal(4, table.Version.Minor);
            Assert.Equal(Subset.Dense(2, 4), table.UpdatesSince(version.Minor));
            Assert.Equal(new[] { R(3, 3), R(1, 9) }, TableTestUtil.DumpSubset(table, table.UpdatesSince(version.Minor).AsRef()).Select(x => x.Row));
            Assert.Throws<ArgumentOutOfRangeException>(() => table.UpdatesSince(5));
        }

        [Fact]
        public void CompactionKeepsRowsInOrderAndStartsANewGeneration()
        {
            var state = TableTestUtil.EmptyState();
            var table = new SortedWritesTable(1, 3, C(2), ReadOnlySpan<ColumnId>.Empty, new KeepIncomingMerge(C(1)), ParallelHeuristics.Serial);
            for (uint ts = 1; ts <= 3; ts++)
            {
                TableTestUtil.Stage(table, Enumerable.Range(0, 20).Select(k => R((uint)k, ts * 100 + (uint)k, ts)).ToArray());
                table.Merge(state);
            }

            // 60 rows written, 40 superseded: more than max(16, 30) tombstones, so the last merge compacted.
            Assert.Equal(1, table.Version.Major);
            Assert.Equal(0, table.StaleCount);
            Assert.Equal(20, table.Rows.Count);
            Assert.Equal(20, table.Count);
            Assert.Equal(1, table.SortRunCount);
            for (uint k = 0; k < 20; k++)
            {
                Assert.Equal((int)k, table.FindRow(k));
                Assert.Equal(R(k, 300 + k, 3), table.GetRow(R(k))!.Value.Values);
            }

            AssertFast(table, Constraint.GeConst(C(2), 3), 0, 20);
            AssertFast(table, Constraint.LtConst(C(2), 3), 0, 0);
        }

        [Fact]
        public void ExplicitRehashRepointsMovedRowsAndRebuildsRuns()
        {
            var state = TableTestUtil.EmptyState();
            var table = new SortedWritesTable(1, 2, C(1), ReadOnlySpan<ColumnId>.Empty, ClosureMerge.TakeNew, ParallelHeuristics.Serial);
            for (uint i = 0; i < 10; i++)
            {
                TableTestUtil.Stage(table, R(i, i / 3));
                table.Merge(state);
            }

            TableTestUtil.StageRemove(table, R(0), R(4), R(5));
            table.Merge(state);
            Assert.Equal(3, table.StaleCount);

            table.Rehash();
            Assert.Equal(1, table.Version.Major);
            Assert.Equal(7, table.Rows.Count);
            Assert.Equal(new uint[] { 1, 2, 3, 6, 7, 8, 9 }, Enumerable.Range(0, 7).Select(r => table.Rows.GetRow(r)[0]));
            foreach (var k in new uint[] { 1, 2, 3, 6, 7, 8, 9 })
                Assert.Equal(R(k, k / 3), table.GetRow(R(k))!.Value.Values);

            Assert.Equal(4, table.SortRunCount);
            AssertFast(table, Constraint.EqConst(C(1), 1), 2, 3);
            AssertFast(table, Constraint.EqConst(C(1), 2), 3, 6);
        }

        [Fact]
        public void ClearDropsRowsAndStagedWritesAndBumpsTheGenerationOnlyWhenNonempty()
        {
            var state = TableTestUtil.EmptyState();
            var table = new SortedWritesTable(1, 2, C(1), ReadOnlySpan<ColumnId>.Empty, ClosureMerge.TakeNew, ParallelHeuristics.Serial);
            table.Clear();
            Assert.Equal(new TableVersion(0, 0), table.Version);

            TableTestUtil.Stage(table, R(1, 1), R(2, 1));
            table.Merge(state);
            TableTestUtil.Stage(table, R(3, 5));
            table.Clear();
            Assert.Equal(new TableVersion(1, 0), table.Version);
            Assert.Equal(0, table.Count);
            Assert.Equal(-1, table.FindRow(1));
            Assert.Equal(new TableChange(false, false), table.Merge(state));
            Assert.Equal(0, table.SortRunCount);

            // Sort order restarts after a clear.
            TableTestUtil.Stage(table, R(1, 0));
            Assert.True(table.Merge(state).Added);
            Assert.Equal(R(1, 0), table.GetRow(R(1))!.Value.Values);
        }

        [Fact]
        public void ClonesAreIndependentAndCopyStagedWrites()
        {
            var state = TableTestUtil.EmptyState();
            var table = TableTestUtil.FillTable([R(1, 10), R(2, 20)], 1, C(1), ClosureMerge.TakeNew);
            var oldBuffer = table.NewBuffer();
            TableTestUtil.Stage(table, R(3, 30));

            var clone = (SortedWritesTable)table.Clone();
            Assert.Equal(table.Version, clone.Version);
            Assert.True(clone.Merge(state).Added);
            Assert.Equal(R(3, 30), clone.GetRow(R(3))!.Value.Values);

            // The original keeps its own staged row.
            Assert.Equal(-1, table.FindRow(3));
            Assert.True(table.Merge(state).Added);
            Assert.Equal(R(3, 30), table.GetRow(R(3))!.Value.Values);

            // A buffer created before the clone still writes to the original only.
            oldBuffer.StageInsert(R(4, 40));
            oldBuffer.Flush();
            table.Merge(state);
            clone.Merge(state);
            Assert.True(table.FindRow(4) >= 0);
            Assert.Equal(-1, clone.FindRow(4));

            TableTestUtil.StageRemove(clone, R(1));
            clone.Merge(state);
            Assert.True(table.FindRow(1) >= 0);
            Assert.Equal(-1, clone.FindRow(1));
        }

        [Fact]
        public void MergeOutputMustKeepTheKeyInDebugBuilds()
        {
            // The parallel and serial paths both store the merge output; it replaces the row under the same key.
            var state = TableTestUtil.EmptyState();
            var merge = new ClosureMerge((current, incoming) => R(current[0], current[1] + incoming[1], incoming[2]));
            var table = new SortedWritesTable(1, 3, C(2), ReadOnlySpan<ColumnId>.Empty, merge, ParallelHeuristics.Serial);
            TableTestUtil.Stage(table, R(1, 1, 1));
            table.Merge(state);
            TableTestUtil.Stage(table, R(1, 2, 2), R(1, 3, 2));
            table.Merge(state);
            Assert.Equal(R(1, 6, 2), table.GetRow(R(1))!.Value.Values);
        }

    }

}

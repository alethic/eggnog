using System;
using System.Collections.Generic;
using System.Linq;

using Xunit;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// Tests for the hidden-aware scans and refinements of every table kind:
    /// <see cref="Table.ScanGenericBounded{TSink}(SubsetRef, int, int, ReadOnlySpan{Constraint}, HiddenRows, ref TSink)"/>,
    /// <see cref="Table.ScanGeneric{TSink}(SubsetRef, HiddenRows, ref TSink)"/>, both <c>ScanProject</c> forms,
    /// <see cref="Table.ForEachColumn{TSink}(SubsetRef, ColumnId, HiddenRows, ref TSink)"/>,
    /// <see cref="Table.RefineVisible"/> and the sealed-set oracle (<see cref="RowVisibilityOracle"/>), against a
    /// reference, for every constraint kind, chunk sizes 1, 7, 64 and unbounded, dense and sparse subsets, with and
    /// without tombstones. With <see cref="HiddenRows.None"/> every result, resume position included, is identical to
    /// the plain overload's.
    /// </summary>
    public class HiddenScanTests
    {

        /// <summary>
        /// The chunk sizes every scan is checked at; <see cref="int.MaxValue"/> is unbounded.
        /// </summary>
        static readonly int[] Chunks = [1, 7, 64, int.MaxValue];

        /// <summary>
        /// Builds a column id.
        /// </summary>
        static ColumnId C(int column) => new(column);

        /// <summary>
        /// A sorted-writes table of <c>[k, a, b, c]</c> rows with small values, so constants and column equalities hit,
        /// and, when <paramref name="tombstones"/>, some keys deleted (tombstones, and possibly a compaction).
        /// </summary>
        static SortedWritesTable RandomTable(Random random, bool tombstones)
        {
            var table = new SortedWritesTable(1, 4, null, [], new KeepCurrentMerge(), ParallelHeuristics.Serial);
            var count = random.Next(0, 400);
            if (count > 0)
            {
                TableTestUtil.Stage(table, Enumerable.Range(0, count).Select(k => new uint[] { (uint)k, (uint)random.Next(6), (uint)random.Next(6), (uint)random.Next(40) }).ToArray());
                table.Merge(TableTestUtil.EmptyState());
            }

            if (tombstones && count > 0)
            {
                var removed = Enumerable.Range(0, count).Where(_ => random.Next(6) == 0).Select(k => new uint[] { (uint)k }).ToArray();
                if (removed.Length > 0)
                {
                    TableTestUtil.StageRemove(table, removed);
                    table.Merge(TableTestUtil.EmptyState());
                }

                // Re-adding some removed keys appends rows after the tombstones.
                var readded = removed.Where(_ => random.Next(3) == 0).Select(k => new uint[] { k[0], (uint)random.Next(6), (uint)random.Next(6), (uint)random.Next(40) }).ToArray();
                if (readded.Length > 0)
                {
                    TableTestUtil.Stage(table, readded);
                    table.Merge(TableTestUtil.EmptyState());
                }
            }

            return table;
        }

        /// <summary>
        /// Builds a sealed hidden set of random rows below <paramref name="count"/>, of every size (so some become
        /// bitsets), adding its rows to <paramref name="reference"/>; or <c>null</c>.
        /// </summary>
        static HiddenSet? RandomSet(Random random, int count, HashSet<int> reference)
        {
            if (count == 0 || random.Next(3) == 0)
                return null;

            var set = new HiddenSet(0);
            var size = random.Next(4) switch
            {
                0 => random.Next(1, 4),
                1 => random.Next(1, Math.Max(2, count / 2)),
                _ => random.Next(1, Math.Max(2, count / 8)),
            };

            for (var i = 0; i < size; i++)
            {
                var row = random.Next(count);
                set.Add(row, count);
                reference.Add(row);
            }

            set.Seal(count);
            return set;
        }

        /// <summary>
        /// Draws hidden rows over a layer of <paramref name="count"/> physical rows: none, a frozen part, a live part,
        /// or both.
        /// </summary>
        static HiddenRows RandomHidden(Random random, int count, out HashSet<int> reference)
        {
            reference = new HashSet<int>();
            return new HiddenRows(RandomSet(random, count, reference), RandomSet(random, count, reference));
        }

        /// <summary>
        /// Draws a subset of <c>[0, count)</c>: empty dense, empty sparse, a dense range, or a sparse run.
        /// </summary>
        static Subset RandomSubset(Random random, int count)
        {
            if (count == 0)
                return random.Next(2) == 0 ? Subset.Empty : OffsetTestUtil.Sparse(Array.Empty<int>());

            switch (random.Next(5))
            {
                case 0:
                    return Subset.Dense(0, count);
                case 1:
                {
                    var start = random.Next(count);
                    return Subset.Dense(start, start + random.Next(0, count - start + 1));
                }
                case 2:
                    return OffsetTestUtil.Sparse(Array.Empty<int>());
                default:
                    return OffsetTestUtil.Sparse(OffsetTestUtil.RandomSorted(random, random.Next(1, count + 1), count, duplicates: false));
            }
        }

        /// <summary>
        /// Draws up to three constraints over columns <c>[0, arity)</c>, covering every kind, with constants near the
        /// values the tables hold (and occasionally the tombstone marker).
        /// </summary>
        static Constraint[] RandomConstraints(Random random, int arity, int valueRange)
        {
            var constraints = new Constraint[random.Next(0, 4)];
            for (var i = 0; i < constraints.Length; i++)
            {
                var column = C(random.Next(arity));
                var constant = new Value(random.Next(12) == 0 ? Value.StaleRep - 1 : (uint)random.Next(valueRange));
                constraints[i] = random.Next(6) switch
                {
                    0 => Constraint.Eq(column, C(random.Next(arity))),
                    1 => Constraint.EqConst(column, constant),
                    2 => Constraint.LtConst(column, constant),
                    3 => Constraint.GtConst(column, constant),
                    4 => Constraint.LeConst(column, constant),
                    _ => Constraint.GeConst(column, constant),
                };
            }

            return constraints;
        }

        /// <summary>
        /// The reference scan: the rows of <paramref name="subset"/>, in order, that are live, not hidden, and satisfy
        /// every constraint, each as its cells.
        /// </summary>
        static List<(int Id, uint[] Row)> Reference(Func<int, uint[]?> cellsOf, SubsetRef subset, Constraint[] constraints, HashSet<int> hidden)
        {
            var result = new List<(int, uint[])>();
            foreach (var row in subset)
            {
                if (cellsOf(row) is not { } cells || hidden.Contains(row))
                    continue;

                if (constraints.All(c => c.Evaluate(cells)))
                    result.Add((row, cells));
            }

            return result;
        }

        /// <summary>
        /// The cells of a physical row of a sorted-writes table, or <c>null</c> for a tombstone.
        /// </summary>
        static Func<int, uint[]?> SortedCells(SortedWritesTable table) => row =>
        {
            var cells = table.Rows.Data.AsSpan(row * table.Arity, table.Arity).ToArray();
            return cells[0] == Value.StaleRep ? null : cells;
        };

        /// <summary>
        /// The expanded cells of a row of a union-find table (never a tombstone).
        /// </summary>
        static Func<int, uint[]?> UnionFindCells(Func<int, int, uint> cell) => row => [cell(row, 0), cell(row, 1), cell(row, 2)];

        /// <summary>
        /// Scans in chunks with the hidden-aware overload, through the base class.
        /// </summary>
        static (List<(int Id, uint[] Row)> Rows, List<int> Positions) ChunkedHidden(Table table, SubsetRef subset, int chunk, Constraint[] constraints, HiddenRows hidden)
        {
            var sink = new RowCollector();
            var positions = new List<int>();
            var position = 0;
            while (position >= 0)
            {
                position = table.ScanGenericBounded(subset, position, chunk, constraints, hidden, ref sink);
                positions.Add(position);
            }

            return (sink.Rows, positions);
        }

        /// <summary>
        /// Scans in chunks with the plain overload, through the base class.
        /// </summary>
        static (List<(int Id, uint[] Row)> Rows, List<int> Positions) ChunkedPlain(Table table, SubsetRef subset, int chunk, Constraint[] constraints)
        {
            var sink = new RowCollector();
            var positions = new List<int>();
            var position = 0;
            while (position >= 0)
            {
                position = table.ScanGenericBounded(subset, position, chunk, constraints, ref sink);
                positions.Add(position);
            }

            return (sink.Rows, positions);
        }

        /// <summary>
        /// Scans in chunks with the sealed sorted-writes table's hidden-aware overload directly.
        /// </summary>
        static (List<(int Id, uint[] Row)> Rows, List<int> Positions) ChunkedSealed(SortedWritesTable table, SubsetRef subset, int chunk, Constraint[] constraints, HiddenRows hidden)
        {
            var sink = new RowCollector();
            var positions = new List<int>();
            var position = 0;
            while (position >= 0)
            {
                position = table.ScanGenericBounded(subset, position, chunk, constraints, hidden, ref sink);
                positions.Add(position);
            }

            return (sink.Rows, positions);
        }

        /// <summary>
        /// Asserts two row lists are equal, ids and cells.
        /// </summary>
        static void AssertRows(List<(int Id, uint[] Row)> expected, List<(int Id, uint[] Row)> actual, string context)
        {
            Assert.True(expected.Count == actual.Count, $"{context}: {actual.Count} rows, expected {expected.Count}");
            for (var i = 0; i < expected.Count; i++)
            {
                Assert.True(expected[i].Id == actual[i].Id, $"{context}: row {i} is {actual[i].Id}, expected {expected[i].Id}");
                Assert.True(expected[i].Row.SequenceEqual(actual[i].Row), $"{context}: row {expected[i].Id} is {TableTestUtil.Show(actual[i].Row)}, expected {TableTestUtil.Show(expected[i].Row)}");
            }
        }

        /// <summary>
        /// Runs every check of a scan family over one table, subset, constraint list and hidden rows.
        /// </summary>
        static void CheckScans(Table table, Func<int, uint[]?> cellsOf, SubsetRef subset, Constraint[] constraints, HiddenRows hidden, HashSet<int> hiddenReference, string context)
        {
            var expected = Reference(cellsOf, subset, constraints, hiddenReference);
            var none = new HashSet<int>();
            var expectedLive = Reference(cellsOf, subset, constraints, none);
            foreach (var chunk in Chunks)
            {
                var (rows, positions) = ChunkedHidden(table, subset, chunk, constraints, hidden);
                AssertRows(expected, rows, $"{context}, chunk {chunk}, hidden");

                // Positions count subset entries, so they do not depend on what is hidden.
                var (plainRows, plainPositions) = ChunkedPlain(table, subset, chunk, constraints);
                Assert.Equal(plainPositions, positions);
                AssertRows(expectedLive, plainRows, $"{context}, chunk {chunk}, plain");

                // HiddenRows.None: identical to the plain overload, call by call.
                var (noneRows, nonePositions) = ChunkedHidden(table, subset, chunk, constraints, HiddenRows.None);
                Assert.Equal(plainPositions, nonePositions);
                AssertRows(plainRows, noneRows, $"{context}, chunk {chunk}, none");

                if (table is SortedWritesTable sorted)
                {
                    var (sealedRows, sealedPositions) = ChunkedSealed(sorted, subset, chunk, constraints, hidden);
                    Assert.Equal(positions, sealedPositions);
                    AssertRows(expected, sealedRows, $"{context}, chunk {chunk}, sealed class");
                }
            }

            // The whole-subset scan.
            var all = new RowCollector();
            table.ScanGeneric(subset, hidden, ref all);
            AssertRows(Reference(cellsOf, subset, [], hiddenReference), all.Rows, context + ", ScanGeneric");
            var allNone = new RowCollector();
            table.ScanGeneric(subset, HiddenRows.None, ref allNone);
            AssertRows(TableTestUtil.DumpSubset(table, subset), allNone.Rows, context + ", ScanGeneric none");
        }

        /// <summary>
        /// Checks the projecting scans, the column walk, the refinement and the sealed-set oracle
        /// (<see cref="RowVisibilityOracle.IsVisible"/>).
        /// </summary>
        static void CheckProjections(Random random, Table table, Func<int, uint[]?> cellsOf, SubsetRef subset, Constraint[] constraints, HiddenRows hidden, HashSet<int> hiddenReference, string context)
        {
            var columns = Enumerable.Range(0, random.Next(1, 5)).Select(_ => C(random.Next(table.Arity))).ToArray();
            var expected = Reference(cellsOf, subset, constraints, hiddenReference).Select(r => (r.Id, columns.Select(c => r.Row[c.Rep]).ToArray())).ToList();
            foreach (var chunk in Chunks)
            {
                var sink = new RowCollector();
                var buffer = new TaggedRowBuffer(columns.Length);
                var plain = new TaggedRowBuffer(columns.Length);
                var none = new TaggedRowBuffer(columns.Length);
                for (var position = 0; position >= 0;)
                {
                    var next = table.ScanProject(subset, columns, position, chunk, constraints, hidden, ref sink);
                    Assert.Equal(next, table.ScanProject(subset, columns, position, chunk, constraints, hidden, buffer));
                    var plainNext = table.ScanProject(subset, columns, position, chunk, constraints, plain);
                    Assert.Equal(plainNext, table.ScanProject(subset, columns, position, chunk, constraints, HiddenRows.None, none));
                    Assert.Equal(plainNext, next);
                    position = next;
                }

                AssertRows(expected, sink.Rows, $"{context}, chunk {chunk}, ScanProject");
                AssertRows(expected, TableTestUtil.DumpBuffer(buffer), $"{context}, chunk {chunk}, ScanProject buffer");
                AssertRows(TableTestUtil.DumpBuffer(plain), TableTestUtil.DumpBuffer(none), $"{context}, chunk {chunk}, ScanProject none");
            }

            var nothing = new RowCollector();
            Assert.Equal(-1, table.ScanProject(subset, ReadOnlySpan<ColumnId>.Empty, 0, int.MaxValue, constraints, hidden, ref nothing));
            Assert.Empty(nothing.Rows);

            // One column of every visible row.
            var column = C(random.Next(table.Arity));
            var columnSink = new RowCollector();
            table.ForEachColumn(subset, column, hidden, ref columnSink);
            var visible = Reference(cellsOf, subset, [], hiddenReference);
            AssertRows(visible.Select(r => (r.Id, new[] { r.Row[column.Rep] })).ToList(), columnSink.Rows, context + ", ForEachColumn");
            var columnNone = new RowCollector();
            var columnPlain = new RowCollector();
            table.ForEachColumn(subset, column, HiddenRows.None, ref columnNone);
            table.ForEachColumn(subset, column, ref columnPlain);
            AssertRows(columnPlain.Rows, columnNone.Rows, context + ", ForEachColumn none");

            // Refinement keeps exactly the visible rows; with nothing hidden it is RefineLive.
            var refined = table.RefineVisible(subset.ToOwned(), hidden);
            Assert.Equal(visible.Select(r => r.Id).ToList(), OffsetTestUtil.Collect(refined));
            refined.Dispose();
            var live = table.RefineLive(subset.ToOwned());
            var liveNone = table.RefineVisible(subset.ToOwned(), HiddenRows.None);
            Assert.Equal(live, liveNone);
            live.Dispose();
            liveNone.Dispose();
        }

        [Fact]
        public void SortedWritesScansSeeOnlyVisibleRows()
        {
            for (var seed = 0; seed < 300; seed++)
            {
                var random = new Random(seed);
                var table = RandomTable(random, tombstones: seed % 2 == 0);
                var count = table.Rows.Count;
                var cellsOf = SortedCells(table);
                for (var trial = 0; trial < 3; trial++)
                {
                    var hidden = RandomHidden(random, count, out var hiddenReference);
                    var subset = RandomSubset(random, count);
                    var constraints = RandomConstraints(random, table.Arity, 8);
                    var context = $"seed {seed} trial {trial} ({subset}, {constraints.Length} constraints, stale {table.HasStaleRows})";
                    CheckScans(table, cellsOf, subset.AsRef(), constraints, hidden, hiddenReference, context);
                    CheckProjections(random, table, cellsOf, subset.AsRef(), constraints, hidden, hiddenReference, context);

                    // The sealed-set oracle agrees with the reference on every physical row.
                    var data = table.Rows.Data;
                    for (var row = 0; row < count; row++)
                        Assert.Equal(cellsOf(row) is not null && hiddenReference.Contains(row) == false, RowVisibilityOracle.IsVisible(data, row * table.Arity, row, hidden.Frozen, hidden.Live));

                    subset.Dispose();
                }
            }
        }

        [Fact]
        public void EveryConstraintKindIsCoveredOnEveryColumn()
        {
            // A fixed table with tombstones and hides, each kind of constraint on each column, at every chunk size.
            var random = new Random(77);
            var table = RandomTable(random, tombstones: true);
            while (table.HasStaleRows == false || table.Rows.Count < 100)
                table = RandomTable(random, tombstones: true);

            var count = table.Rows.Count;
            var hidden = RandomHidden(random, count, out var hiddenReference);
            while (hidden.Frozen is null || hidden.Live is null)
                hidden = RandomHidden(random, count, out hiddenReference);

            var cellsOf = SortedCells(table);
            var subset = Subset.Dense(0, count);
            for (var column = 0; column < table.Arity; column++)
            {
                foreach (var constant in new uint[] { 0, 3, 5, Value.StaleRep - 1 })
                {
                    var value = new Value(constant);
                    Constraint[][] lists =
                    [
                        [Constraint.Eq(C(column), C((column + 1) % table.Arity))],
                        [Constraint.EqConst(C(column), value)],
                        [Constraint.LtConst(C(column), value)],
                        [Constraint.GtConst(C(column), value)],
                        [Constraint.LeConst(C(column), value)],
                        [Constraint.GeConst(C(column), value)],
                    ];

                    foreach (var constraints in lists)
                        CheckScans(table, cellsOf, subset.AsRef(), constraints, hidden, hiddenReference, $"column {column} constant {constant} {constraints[0].Kind}");
                }
            }
        }

        [Fact]
        public void HiddenRowsAreDroppedBeforeConstraintsSeeThem()
        {
            // A hidden row whose cells satisfy the constraint is still dropped, and a column-0 constraint never sees a
            // tombstone's marker.
            var table = new SortedWritesTable(1, 2, null, [], new KeepCurrentMerge(), ParallelHeuristics.Serial);
            TableTestUtil.Stage(table, [0, 7], [1, 7], [2, 7], [3, 7]);
            table.Merge(TableTestUtil.EmptyState());
            TableTestUtil.StageRemove(table, [2]);
            table.Merge(TableTestUtil.EmptyState());
            var set = new HiddenSet(0);
            set.Add(1, table.Rows.Count);
            set.Seal(table.Rows.Count);
            var hidden = new HiddenRows(null, set);

            var sink = new RowCollector();
            var all = table.All();
            table.ScanGenericBounded(all.AsRef(), 0, int.MaxValue, [Constraint.GtConst(C(0), new Value(0)), Constraint.EqConst(C(1), new Value(7))], hidden, ref sink);
            Assert.Equal(new[] { 3 }, sink.Rows.Select(r => r.Id));
            Assert.Equal(new[] { 0, 3 }, OffsetTestUtil.Collect(table.RefineVisible(table.All(), hidden)));
        }

        [Fact]
        public void UnionFindScansSeeOnlyVisibleRows()
        {
            for (var seed = 0; seed < 150; seed++)
            {
                var random = new Random(1000 + seed);
                var uf = new DisplacedTable();
                var ts = 1u;
                var unions = random.Next(0, 120);
                for (var i = 0; i < unions; i++)
                {
                    ts += (uint)random.Next(2);
                    TableTestUtil.Stage(uf, [(uint)random.Next(1, 90), (uint)random.Next(1, 90), ts]);
                    if (random.Next(4) == 0)
                        uf.Merge(TableTestUtil.EmptyState());
                }

                uf.Merge(TableTestUtil.EmptyState());
                CheckUnionFind(random, uf, UnionFindCells(uf.GetCell), $"displaced seed {seed}");
            }
        }

        [Fact]
        public void ChildUnionFindScansSeeOnlyVisibleRows()
        {
            for (var seed = 0; seed < 60; seed++)
            {
                var random = new Random(2000 + seed);
                var store = new Store(ParallelHeuristics.Serial);
                for (var i = random.Next(0, 30); i > 0; i--)
                    store.Root.Union((uint)random.Next(1, 60), (uint)random.Next(1, 60));

                store.Root.Rebuild();
                var child = store.Root.CreateChild();
                for (var i = random.Next(0, 60); i > 0; i--)
                    child.Union((uint)random.Next(1, 90), (uint)random.Next(1, 90));

                child.Rebuild();
                var overlay = Assert.IsType<OverlayDisplacedTable>(child.UnionFindTable);
                CheckUnionFind(random, overlay, UnionFindCells(overlay.GetCell), $"overlay seed {seed}");
                child.Dispose();
            }
        }

        /// <summary>
        /// Runs the scan checks over a union-find table, whose column 1 is computed.
        /// </summary>
        static void CheckUnionFind(Random random, Table table, Func<int, uint[]?> cellsOf, string context)
        {
            var count = table.Count;
            for (var trial = 0; trial < 3; trial++)
            {
                var hidden = RandomHidden(random, count, out var hiddenReference);
                var subset = RandomSubset(random, count);
                var constraints = RandomConstraints(random, 3, 90);
                CheckScans(table, cellsOf, subset.AsRef(), constraints, hidden, hiddenReference, $"{context} trial {trial}");
                CheckProjections(random, table, cellsOf, subset.AsRef(), constraints, hidden, hiddenReference, $"{context} trial {trial}");
                subset.Dispose();
            }
        }

    }

}

using System;
using System.Collections.Generic;
using System.Linq;

using Xunit;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// Model-based randomized tests of <see cref="SortedWritesTable"/> against <see cref="SortedWritesModel"/>, a
    /// dictionary with fold semantics, for every built-in merge: random batches of inserts and removals with key
    /// collisions inside and across batches, tombstones, automatic compaction, clearing and cloning, checking contents,
    /// change reports, scans, constrained scans, fast subsets and the table's internal invariants after every merge.
    /// </summary>
    public class SortedWritesTableModelTests
    {

        static ColumnId C(int column) => new(column);

        /// <summary>
        /// Gets every (merge, key count, sorted) combination.
        /// </summary>
        public static TheoryData<string, int, bool> Cases()
        {
            var data = new TheoryData<string, int, bool>();
            foreach (var merge in new[] { "keep-current", "keep-incoming", "min", "max", "assert-equal", "union-id", "sum" })
                foreach (var keyCount in new[] { 1, 2, 4 })
                    foreach (var sorted in new[] { true, false })
                        data.Add(merge, keyCount, sorted);

            data.Add("keep-incoming", 0, true);
            data.Add("sum", 0, false);
            return data;
        }

        [Theory]
        [MemberData(nameof(Cases))]
        public void MatchesTheFoldModel(string mergeName, int keyCount, bool sorted)
        {
            for (var seed = 0; seed < 3; seed++)
                Run(mergeName, keyCount, sorted, seed);
        }

        static void Run(string mergeName, int keyCount, bool sorted, int seed)
        {
            var random = new Random(HashCode.Combine(mergeName, keyCount, sorted, seed) & 0x7fffffff);
            var arity = keyCount + 2;
            var valueColumn = keyCount;
            var tsColumn = keyCount + 1;
            var keyDomain = keyCount switch { 0 => 1, 1 => 40, 2 => 7, _ => 3 };
            var valueDomain = mergeName == "sum" ? 4 : 16;

            var catalog = new TableCatalog();
            var uf = new DisplacedTable();
            var ufId = catalog.Add(uf);
            var unions = new PartitionReference(valueDomain);
            var conflicts = 0;

            MergeFunction merge = mergeName switch
            {
                "keep-current" => KeepCurrentMerge.Instance,
                "keep-incoming" => new KeepIncomingMerge(C(valueColumn)),
                "min" => new MinMerge(C(valueColumn)),
                "max" => new MaxMerge(C(valueColumn)),
                "assert-equal" => new AssertEqualMerge(C(valueColumn), "f"),
                "union-id" => new UnionIdMerge(C(valueColumn), ufId, sorted ? C(tsColumn) : null),
                _ => new SumMerge(valueColumn),
            };

            Func<uint[], uint[], uint[]?> model = mergeName switch
            {
                "keep-current" => (_, _) => null,
                "keep-incoming" => (c, i) => i[valueColumn] != c[valueColumn] ? i : null,
                "min" => (c, i) => i[valueColumn] < c[valueColumn] ? i : null,
                "max" => (c, i) => i[valueColumn] > c[valueColumn] ? i : null,
                "assert-equal" => (c, i) =>
                {
                    if (c[valueColumn] != i[valueColumn])
                        conflicts++;

                    return null;
                },
                "union-id" => (c, i) =>
                {
                    if (c[valueColumn] != i[valueColumn])
                        unions.Union(c[valueColumn], i[valueColumn], out _, out _);

                    return i[valueColumn] < c[valueColumn] ? i : null;
                },
                _ => (c, i) =>
                {
                    if (i[valueColumn] == 0)
                        return null;

                    var result = i.ToArray();
                    result[valueColumn] = c[valueColumn] + i[valueColumn];
                    return result;
                },
            };

            var parallelism = seed % 2 == 0 ? ParallelHeuristics.Serial : new ParallelHeuristics(4);
            var table = new SortedWritesTable(keyCount, arity, sorted ? C(tsColumn) : null, ReadOnlySpan<ColumnId>.Empty, merge, parallelism);
            catalog.Add(table);
            var reference = new SortedWritesModel(keyCount);
            uint timestamp = 0;
            var compactions = 0;

            uint[] RandomKey()
            {
                var key = new uint[keyCount];
                for (var c = 0; c < keyCount; c++)
                    key[c] = (uint)random.Next(keyDomain);

                return key;
            }

            for (var step = 0; step < 120; step++)
            {
                var roll = random.Next(100);
                if (roll < 3)
                {
                    var hadRows = table.Rows.Count > 0;
                    var major = table.Version.Major;
                    table.Clear();
                    reference.Clear();
                    Assert.Equal(hadRows ? major + 1 : major, table.Version.Major);
                    Check(table, reference, valueColumn, tsColumn, sorted, random);
                    continue;
                }

                if (roll < 6)
                {
                    var clone = (SortedWritesTable)table.Clone();
                    Assert.Equal(table.Version, clone.Version);
                    catalog.Set(new TableId(1), clone);
                    table = clone;
                    Check(table, reference, valueColumn, tsColumn, sorted, random);
                    continue;
                }

                timestamp++;
                var removals = new List<uint[]>();
                var inserts = new List<uint[]>();
                var first = table.NewBuffer();
                var second = table.NewBuffer();
                var removalCount = random.Next(6);
                for (var i = 0; i < removalCount; i++)
                {
                    var key = RandomKey();
                    removals.Add(key);
                    (random.Next(2) == 0 ? first : second).StageRemove(key);
                }

                var firstRows = new List<uint[]>();
                var secondRows = new List<uint[]>();
                var insertCount = random.Next(30);
                for (var i = 0; i < insertCount; i++)
                {
                    var row = new uint[arity];
                    RandomKey().CopyTo(row, 0);
                    row[valueColumn] = (uint)random.Next(valueDomain);
                    row[tsColumn] = timestamp;
                    if (random.Next(2) == 0)
                    {
                        first.StageInsert(row);
                        firstRows.Add(row);
                    }
                    else
                    {
                        second.StageInsert(row);
                        secondRows.Add(row);
                    }
                }

                // Rows of one key are processed in queue order (first flushed first), then staging order.
                first.Flush();
                second.Flush();
                inserts.AddRange(firstRows);
                inserts.AddRange(secondRows);

                var state = new ExecutionState(catalog, timestamp);
                var conflictsBefore = conflicts;
                var majorBefore = table.Version.Major;
                var change = table.Merge(state);
                if (table.Version.Major != majorBefore)
                    compactions++;

                var expected = reference.Apply(removals, inserts, model);
                Assert.Equal(expected, change);

                if (mergeName == "union-id")
                {
                    state.Flush();
                    uf.Merge(state);
                    for (uint v = 0; v < valueDomain; v++)
                        Assert.Equal(unions.Canonical(v), uf.FindNaive(v));
                }

                if (mergeName == "assert-equal")
                    Assert.Equal(conflicts > conflictsBefore, state.Failure is not null);

                Check(table, reference, valueColumn, tsColumn, sorted, random);
            }

            // The churning merges must have exercised compaction.
            if (keyCount > 0 && (mergeName == "keep-incoming" || mergeName == "sum"))
                Assert.True(compactions > 0, "no compaction happened");
        }

        static void Check(SortedWritesTable table, SortedWritesModel reference, int valueColumn, int tsColumn, bool sorted, Random random)
        {
            var keyCount = table.KeyCount;
            Assert.Equal(reference.Count, table.Count);

            // Point lookups.
            foreach (var row in reference.Rows)
            {
                var id = table.FindRow(row.AsSpan(0, keyCount));
                Assert.True(id >= 0, "missing " + TableTestUtil.Show(row));
                Assert.Equal(row, table.Rows.GetRow(id).ToArray());
            }

            // Whole scans see exactly the model's rows.
            var expected = reference.Rows.Select(TableTestUtil.Show).Order().ToList();
            Assert.Equal(expected, TableTestUtil.LiveRows(table).Select(TableTestUtil.Show).Order().ToList());

            // Internal invariants: exact tombstone count, compaction threshold, version.
            var physical = table.Rows.Count;
            var stale = Enumerable.Range(0, physical).Count(r => table.Rows.IsStale(r));
            Assert.Equal(stale, table.StaleCount);
            Assert.True(table.StaleCount <= Math.Max(16, physical / 2));
            Assert.Equal(physical, table.Version.Minor);
            Assert.Equal(stale > 0, table.HasStaleRows);

            // A constrained scan on the value column sees no tombstones.
            var probe = (uint)random.Next(16);
            var matching = new RowCollector();
            var next = 0;
            while (next >= 0)
                next = table.ScanGenericBounded(table.All().AsRef(), next, 7, [Constraint.EqConst(C(valueColumn), probe)], ref matching);

            Assert.Equal(
                reference.Rows.Where(r => r[valueColumn] == probe).Select(TableTestUtil.Show).Order(),
                matching.Rows.Select(x => TableTestUtil.Show(x.Row)).Order());

            if (sorted == false)
                return;

            // The sort column is nondecreasing over every physical row, tombstones included.
            for (var r = 1; r < physical; r++)
                Assert.True(table.Rows.GetRow(r - 1)[tsColumn] <= table.Rows.GetRow(r)[tsColumn]);

            // Fast subsets on the timestamp, read through the table, give the model's rows in range.
            var maxTs = reference.Rows.Select(r => r[tsColumn]).DefaultIfEmpty(0u).Max();
            var t = (uint)random.Next((int)maxTs + 2);
            foreach (var (constraint, keep) in new (Constraint, Func<uint, bool>)[]
            {
                (Constraint.GeConst(C(tsColumn), t), ts => ts >= t),
                (Constraint.LtConst(C(tsColumn), t), ts => ts < t),
                (Constraint.EqConst(C(tsColumn), t), ts => ts == t),
                (Constraint.GtConst(C(tsColumn), t), ts => ts > t),
                (Constraint.LeConst(C(tsColumn), t), ts => ts <= t),
            })
            {
                Assert.True(table.TryFastSubset(constraint, out var subset));
                var rows = TableTestUtil.DumpSubset(table, subset.AsRef()).Select(x => TableTestUtil.Show(x.Row)).Order().ToList();
                Assert.Equal(reference.Rows.Where(r => keep(r[tsColumn])).Select(TableTestUtil.Show).Order().ToList(), rows);
                var live = table.RefineLive(subset);
                Assert.Equal(rows.Count, live.Size);
                live.Dispose();
            }
        }

    }

}

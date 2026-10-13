using System;
using System.Collections.Generic;
using System.Linq;

using Xunit;

using static Eggnog.Relations.Tests.DatabaseTestUtil;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// Tests for <see cref="Database.MergeAll"/> and <see cref="Database.MergeTable"/> (egglog's
    /// core-relations/src/free_join/mod.rs:501-623), including the stratum-order fix of map §8.1 row 11.
    /// </summary>
    public class DatabaseMergeTests
    {

        [Fact]
        public void MergeStatesCarryTheTimestampOfTheRun()
        {
            // Map section 4.2 C1: the rows merge functions write carry the epoch captured for the run being merged.
            var db = new Database(ParallelHeuristics.Serial);
            var log = db.AddTable(Function(1, KeepCurrentMerge.Instance), name: "log");
            var f = db.AddTable(Function(1, new StampingMerge(log, 1)), [], [log], "f");
            Stage(db, f, R(1, 10, 1));
            db.MergeAll(1);
            Stage(db, f, R(1, 20, 4));
            Assert.True(db.MergeAll(4));
            Assert.Equal(new[] { R(10, 20, 4) }, Rows(db, log));

            // MergeTable stamps the same way; the write it stages waits for the next MergeAll.
            Stage(db, f, R(1, 30, 6));
            db.MergeTable(f, 6);
            Assert.True(db.MergeAll(6));
            Assert.Equal(new[] { R(10, 20, 4), R(20, 30, 6) }, Rows(db, log));
        }

        [Fact]
        public void ParallelMergeStatesCarryTheTimestampOfTheRun()
        {
            var parallelism = new ParallelHeuristics(4);
            var db = new Database(parallelism);
            var log = db.AddTable(Function(1, KeepCurrentMerge.Instance, parallelism: parallelism), name: "log");
            var f = db.AddTable(Function(1, new StampingMerge(log, 1), parallelism: parallelism), [], [log], "f");
            var g = db.AddTable(Function(1, new StampingMerge(log, 1), parallelism: parallelism), [], [log], "g");
            Stage(db, f, Enumerable.Range(0, 6000).Select(i => R((uint)i, (uint)i, 1)).ToArray());
            Stage(db, g, Enumerable.Range(0, 6000).Select(i => R((uint)i, (uint)(10_000 + i), 1)).ToArray());
            db.MergeAll(1);
            Assert.True(db.Parallelism.ParallelizeDbLevelOp(db.TotalSizeEstimate));

            Stage(db, f, R(1, 100, 9));
            Stage(db, g, R(2, 200, 9));
            Assert.True(db.MergeAll(9));
            Assert.Equal(new[] { R(1, 100, 9), R(10_002, 200, 9) }, Rows(db, log).OrderBy(r => r[1]));
        }

        [Fact]
        public void MergeAllReportsAddedRowsAndStagedWritesOnly()
        {
            var db = new Database(ParallelHeuristics.Serial);
            var f = db.AddTable(Function(1, KeepCurrentMerge.Instance));
            Assert.False(db.MergeAll());

            Stage(db, f, R(1, 10, 0), R(2, 20, 0));
            Assert.True(db.MergeAll());

            // Re-deriving present facts changes nothing.
            Stage(db, f, R(1, 11, 1));
            Assert.False(db.MergeAll());

            // Removals alone do not count as a change (egglog's `added || es.changed`).
            StageRemove(db, f, R(1));
            Assert.False(db.MergeAll());
            Assert.Equal(new[] { R(2, 20, 0) }, Rows(db, f));
        }

        [Fact]
        public void MergeAllMergesInStratumOrderEvenForSmallRounds()
        {
            // Map §8.1 row 11: F reads G in its merge. Notify F first, then G. egglog's merge_simple merges F before G,
            // so F's merge reads G's old value 10; stratum order merges G first, so F reads 20.
            var db = new Database(ParallelHeuristics.Serial);
            var g = db.AddTable(Function(1, ClosureMerge.TakeNew));
            var f = db.AddTable(Function(1, new LookupMerge(g, 1, 1, 1)), [g]);
            Stage(db, f, R(1, 0, 0));
            Stage(db, g, R(1, 10, 0));
            db.MergeAll();

            Stage(db, f, R(1, 5, 1));
            Stage(db, g, R(1, 20, 1));
            Assert.True(db.MergeAll());
            Assert.Equal(new[] { R(1, 20, 1) }, Rows(db, f));
            Assert.Equal(new[] { R(1, 20, 1) }, Rows(db, g));
        }

        [Fact]
        public void MergeAllMergesInStratumOrderForLargeRounds()
        {
            var db = new Database(ParallelHeuristics.Serial);
            var fillers = new List<TableId>();
            for (var i = 0; i < 3; i++)
                fillers.Add(db.AddTable(Function(1, ClosureMerge.TakeNew)));

            var g = db.AddTable(Function(1, ClosureMerge.TakeNew));
            var f = db.AddTable(Function(1, new LookupMerge(g, 1, 1, 1)), [g]);
            var h = db.AddTable(Function(1, new LookupMerge(f, 1, 1, 1)), [f]);
            Assert.Equal(2, db.Dependencies.LevelOf(h));
            Stage(db, h, R(1, 0, 0));
            Stage(db, f, R(1, 0, 0));
            Stage(db, g, R(1, 10, 0));
            db.MergeAll();

            Stage(db, h, R(1, 7, 1));
            Stage(db, f, R(1, 5, 1));
            foreach (var filler in fillers)
                Stage(db, filler, R(9, 9, 1));

            Stage(db, g, R(1, 20, 1));
            Assert.True(db.MergeAll());
            Assert.Equal(new[] { R(1, 20, 1) }, Rows(db, f));
            Assert.Equal(new[] { R(1, 20, 1) }, Rows(db, h));
        }

        [Fact]
        public void AMergeCannotReadATableOfItsOwnStratumBeingMerged()
        {
            // F reads G without declaring it, so both are in stratum 0 and are taken out together: the read fails, as
            // indexing egglog's table_info panics. The database stays consistent and keeps H, not merged yet, notified.
            var db = new Database(ParallelHeuristics.Serial);
            var g = db.AddTable(Function(1, ClosureMerge.TakeNew));
            var f = db.AddTable(Function(1, new LookupMerge(g, 1, 1, 1)));
            var h = db.AddTable(Function(1, ClosureMerge.TakeNew));
            Stage(db, f, R(1, 0, 0));
            Stage(db, g, R(1, 10, 0));
            db.MergeAll();

            Stage(db, g, R(1, 20, 1));
            Stage(db, f, R(1, 5, 1));
            Stage(db, h, R(4, 40, 1));
            var e = Assert.Throws<KeyNotFoundException>(() => db.MergeAll());
            Assert.Contains("taken out", e.Message);

            Assert.Same(db.Catalog[f], db.GetTable(f));
            Assert.Same(db.Catalog[g], db.GetTable(g));
            Assert.Same(db.Catalog[h], db.GetTable(h));
            Assert.Equal(new[] { R(1, 20, 1) }, Rows(db, g));
            Assert.Empty(Rows(db, h));

            // F's failed batch was dropped by the table; H is merged by the next call.
            Assert.True(db.MergeAll());
            Assert.Equal(new[] { R(1, 0, 0) }, Rows(db, f));
            Assert.Equal(new[] { R(4, 40, 1) }, Rows(db, h));
            Assert.Equal(3, db.TotalSizeEstimate);
        }

        [Fact]
        public void MergeAllResetsIndexesAndRecomputesTheSizeEstimate()
        {
            var (db, uf, num, add, _) = DatabaseTests.BasicMathEgraph();
            Stage(db, num, R(1, 10, 0));
            db.MergeAll();
            using (var processed = db.ProcessConstraints(num, [Constraint.EqConst(C(1), 11)]))
                Assert.Equal(0, processed.ApproximateSize);

            Stage(db, num, R(2, 11, 1), R(3, 11, 1));
            Stage(db, add, R(1, 2, 12, 1));
            db.MergeAll();
            Assert.Equal(4, db.TotalSizeEstimate);
            using (var processed = db.ProcessConstraints(num, [Constraint.EqConst(C(1), 11)]))
                Assert.Equal(new[] { R(2, 11, 1), R(3, 11, 1) }, Rows(db.GetTable(num), processed.Rows));

            // A union counts towards the estimate too: the union-find has one row.
            Stage(db, num, R(1, 12, 2));
            db.MergeAll();
            Assert.Equal(1, db.GetTable(uf).Count);
            Assert.Equal(5, db.TotalSizeEstimate);
        }

        [Fact]
        public void ParallelStrataAgreeWithSerialMerging()
        {
            static (Database Db, TableId Uf, TableId[] Tables) Build(ParallelHeuristics parallelism)
            {
                var db = new Database(parallelism);
                var uf = db.AddTable(new DisplacedTable());
                var tables = new TableId[6];
                for (var t = 0; t < tables.Length; t++)
                    tables[t] = db.AddTable(Function(1, new UnionIdMerge(C(1), uf, C(2)), [C(0), C(1)], parallelism), [], [uf]);

                for (var t = 0; t < tables.Length; t++)
                    Stage(db, tables[t], Enumerable.Range(0, 3000).Select(i => R((uint)i, (uint)(100_000 + t * 10_000 + i), 1)).ToArray());

                db.MergeAll();
                return (db, uf, tables);
            }

            var (serial, serialUf, serialTables) = Build(ParallelHeuristics.Serial);
            var (parallel, parallelUf, parallelTables) = Build(new ParallelHeuristics(4));
            Assert.True(parallel.Parallelism.ParallelizeDbLevelOp(parallel.TotalSizeEstimate));
            Assert.False(serial.Parallelism.ParallelizeDbLevelOp(serial.TotalSizeEstimate));

            // Every table gets conflicting rows whose union-id merges stage unions into the union-find.
            foreach (var (db, tables) in new[] { (serial, serialTables), (parallel, parallelTables) })
            {
                for (var t = 0; t < tables.Length; t++)
                    Stage(db, tables[t], Enumerable.Range(0, 3000).Where(i => i % 3 == t % 3).Select(i => R((uint)i, (uint)(50_000 + i), 2)).ToArray());

                Assert.True(db.MergeAll());
            }

            for (var t = 0; t < serialTables.Length; t++)
                Assert.Equal(Dump(serial, serialTables[t]), Dump(parallel, parallelTables[t]));

            var serialFind = (DisplacedTable)serial.GetTable(serialUf);
            var parallelFind = (DisplacedTable)parallel.GetTable(parallelUf);
            Assert.Equal(serialFind.Count, parallelFind.Count);
            for (var id = 50_000u; id < 170_000; id += 7)
                Assert.Equal(serialFind.FindNaive(id), parallelFind.FindNaive(id));
        }

        [Fact]
        public void MergeTableMergesOneTableAndResetsItsIndexes()
        {
            var (db, uf, num, _, _) = DatabaseTests.BasicMathEgraph();
            Stage(db, num, R(1, 10, 0));
            db.MergeAll();
            using (var processed = db.ProcessConstraints(num, [Constraint.EqConst(C(1), 11)]))
                Assert.Equal(0, processed.ApproximateSize);

            Stage(db, num, R(2, 11, 1), R(1, 12, 1));
            Assert.True(db.MergeTable(num));
            Assert.Equal(2, db.TotalSizeEstimate);

            // egglog would answer from the stale index here.
            using (var processed = db.ProcessConstraints(num, [Constraint.EqConst(C(1), 11)]))
                Assert.Equal(new[] { R(2, 11, 1) }, Rows(db.GetTable(num), processed.Rows));

            // The union the merge staged is notified for the next MergeAll; the table itself is still notified.
            Assert.Equal(0, db.GetTable(uf).Count);
            Assert.True(db.MergeAll());
            Assert.Equal(1, db.GetTable(uf).Count);

            Assert.False(db.MergeTable(num));
            Assert.Throws<KeyNotFoundException>(() => db.MergeTable(new TableId(9)));
        }

    }

}

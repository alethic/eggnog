using System;
using System.Collections.Generic;
using System.Linq;

using Xunit;

using static Eggnog.Relations.Tests.DatabaseTestUtil;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// Adversarial review tests for <see cref="Database"/>: paths the implementation's own tests do not reach.
    /// </summary>
    public class DatabaseReviewTests
    {

        [Fact]
        public void MergeTableThatThrowsKeepsTheSizeEstimate()
        {
            // The class promises that a merge that throws leaves the database consistent. MergeTable subtracts the
            // table's size before merging and adds it back only on success.
            var db = new Database(ParallelHeuristics.Serial);
            var f = db.AddTable(Function(1, KeepCurrentMerge.Instance));
            Stage(db, f, R(1, 10, 5));
            db.MergeAll();
            Assert.Equal(1, db.TotalSizeEstimate);

            // Timestamp 3 after timestamp 6 breaks the sort order of the timestamp column: the merge throws.
            Stage(db, f, R(2, 20, 6), R(3, 30, 3));
            Assert.ThrowsAny<Exception>(() => db.MergeTable(f));

            Assert.Equal(db.GetTable(f).Count, db.TotalSizeEstimate);
        }

        [Fact]
        public void MergeTableThatThrowsResetsTheCachedIndexes()
        {
            // MergeAll resets every cache in a finally; MergeTable resets the table's caches only on success, so after a
            // throw the armed column index keeps answering for the table as it was before the rows the merge did add.
            var db = new Database(ParallelHeuristics.Serial);
            var f = db.AddTable(Function(1, KeepCurrentMerge.Instance));
            Stage(db, f, R(1, 10, 5));
            db.MergeAll();
            using (var before = db.ProcessConstraints(f, [Constraint.EqConst(C(1), 20)]))
                Assert.Equal(0, before.ApproximateSize);

            Stage(db, f, R(2, 20, 6), R(3, 30, 3));
            Assert.ThrowsAny<Exception>(() => db.MergeTable(f));

            var live = Rows(db, f);
            Assert.Contains(live, r => r.SequenceEqual(R(2, 20, 6)));
            using var after = db.ProcessConstraints(f, [Constraint.EqConst(C(1), 20)]);
            Assert.Equal(new[] { R(2, 20, 6) }, Rows(db.GetTable(f), after.Rows));
        }

        [Fact]
        public void MergeAllThatThrowsResetsTheCachedIndexes()
        {
            // The same failure through MergeAll: the finally resets the caches, so the row the merge added is found.
            var db = new Database(ParallelHeuristics.Serial);
            var f = db.AddTable(Function(1, KeepCurrentMerge.Instance));
            Stage(db, f, R(1, 10, 5));
            db.MergeAll();
            using (var before = db.ProcessConstraints(f, [Constraint.EqConst(C(1), 20)]))
                Assert.Equal(0, before.ApproximateSize);

            Stage(db, f, R(2, 20, 6), R(3, 30, 3));
            Assert.ThrowsAny<Exception>(() => db.MergeAll());

            Assert.Equal(db.GetTable(f).Count, db.TotalSizeEstimate);
            if (Rows(db, f).Any(r => r.SequenceEqual(R(2, 20, 6))))
            {
                using var after = db.ProcessConstraints(f, [Constraint.EqConst(C(1), 20)]);
                Assert.Equal(new[] { R(2, 20, 6) }, Rows(db.GetTable(f), after.Rows));
            }
        }

        [Fact]
        public void AnUndeclaredSameStratumReadThrowsKeyNotFoundWhenParallelToo()
        {
            // The deviation note promises KeyNotFoundException "regardless of round size" for a merge that reads an
            // undeclared table of its own stratum being merged. Above the database-level threshold the stratum merges
            // through Parallel.For, which wraps the exception.
            var parallelism = new ParallelHeuristics(4);
            var db = new Database(parallelism);
            var g = db.AddTable(Function(1, ClosureMerge.TakeNew, parallelism: parallelism));
            var f = db.AddTable(Function(1, new LookupMerge(g, 1, 1, 1), parallelism: parallelism));
            Stage(db, f, Enumerable.Range(0, 6000).Select(i => R((uint)i, 0, 0)).ToArray());
            Stage(db, g, Enumerable.Range(0, 6000).Select(i => R((uint)i, 10, 0)).ToArray());
            db.MergeAll();
            Assert.True(db.Parallelism.ParallelizeDbLevelOp(db.TotalSizeEstimate));

            Stage(db, g, R(1, 20, 1));
            Stage(db, f, R(1, 5, 1));
            Assert.Throws<KeyNotFoundException>(() => db.MergeAll());
        }

        [Fact]
        public void ParallelRebuildMergesReadDependenciesFirst()
        {
            // Map section 8.1 row 11 in the parallel run_on_tables arm: F's merge reads G; both are rebuilt in parallel
            // and notified in completion order. G must be merged before F reads it.
            for (var attempt = 0; attempt < 20; attempt++)
            {
                var parallelism = new ParallelHeuristics(4);
                var db = new Database(parallelism);
                var uf = db.AddTable(new DisplacedTable(), name: "$uf");
                var g = db.AddTable(Function(1, ClosureMerge.TakeNew, [C(0)], parallelism), [], [], "g");
                var f = db.AddTable(Function(1, new LookupMerge(g, 1, 1, 1), [C(0)], parallelism), [g], [], "f");
                Stage(db, g, Enumerable.Range(0, 6000).Select(i => R((uint)(1000 + i), 7, 0)).Concat([R(1, 100, 0), R(2, 200, 0)]).ToArray());
                Stage(db, f, Enumerable.Range(0, 6000).Select(i => R((uint)(1000 + i), 7, 0)).Concat([R(1, 0, 0), R(2, 0, 0)]).ToArray());
                db.MergeAll();
                Stage(db, uf, R(1, 2, 1));
                db.MergeAll();
                Assert.True(db.Parallelism.ParallelizeDbLevelOp(db.TotalSizeEstimate));

                db.ApplyRebuild(uf, [f, g], 2);
                var gRow = Rows(db, g).Single(r => r[0] == 1);
                var fRow = Rows(db, f).Single(r => r[0] == 1);
                Assert.Equal(gRow[1], fRow[1]);
            }
        }

        [Fact]
        public void ZeroKeyTablesMergeAndAnswerConstraints()
        {
            // A nullary function: no key columns, one value and a timestamp.
            var db = new Database(ParallelHeuristics.Serial);
            var f = db.AddTable(Function(0, ClosureMerge.TakeNew));
            Stage(db, f, R(5, 0));
            Assert.True(db.MergeAll());
            Stage(db, f, R(6, 1));
            Assert.True(db.MergeAll());
            Assert.Equal(new[] { R(6, 1) }, Rows(db, f));
            Assert.Equal(1, db.EstimateSize(f));
            using (var processed = db.ProcessConstraints(f, [Constraint.EqConst(C(0), 6)]))
                Assert.Equal(new[] { R(6, 1) }, Rows(db.GetTable(f), processed.Rows));

            var uniqueness = new ColumnCardEstimator(db).GetColumnUniqueness(f, C(0));
            Assert.Equal(1ul, uniqueness.TableSize);
            Assert.Equal(1, db.TotalSizeEstimate);
        }

        [Fact]
        public void RebuildMergesStampUnionsWithTheRebuildTimestamp()
        {
            // UnionIdMerge without a timestamp column stamps its union with ExecutionState.Timestamp. ApplyRebuild knows
            // the timestamp of the pass (its states carry it), but the MergeAll it runs gives every merge state
            // timestamp 0, so the union staged by the congruence it finds goes backwards in the union-find's timestamp
            // column, which the union-find rejects.
            var db = new Database(ParallelHeuristics.Serial);
            var uf = db.AddTable(new DisplacedTable(), name: "$uf");
            var f = db.AddTable(Function(1, new UnionIdMerge(C(1), uf), [C(0), C(1)]), [], [uf], "f");
            Stage(db, f, R(1, 10, 1), R(2, 20, 1));
            db.MergeAll();
            Stage(db, uf, R(1, 2, 2));
            db.MergeAll();

            // f(2) = 20 becomes f(1) = 20, which collides with f(1) = 10 and unions 10 and 20 at the rebuild's timestamp.
            db.ApplyRebuild(uf, [f], 3);
            Assert.Equal(10u, ((DisplacedTable)db.GetTable(uf)).FindNaive(20));
            Assert.Null(db.Failure);
        }

        [Fact]
        public void RowsMergeFunctionsWriteDuringARebuildCarryThePassTimestamp()
        {
            // Map section 4.2 C1: every write of a rebuild pass, including writes made by merge functions, carries the
            // pass's captured timestamp. The merge states of the MergeAll inside ApplyRebuild carry 0, so a merge that
            // writes into a table sorted by timestamp breaks its sort order once that table holds later rows.
            var db = new Database(ParallelHeuristics.Serial);
            var uf = db.AddTable(new DisplacedTable(), name: "$uf");
            var log = db.AddTable(Function(1, KeepCurrentMerge.Instance), name: "log");
            var f = db.AddTable(Function(1, new StampingMerge(log, 1), [C(0)]), [], [log], "f");
            Stage(db, log, R(100, 100, 2));
            Stage(db, f, R(1, 10, 1), R(2, 20, 1));
            db.MergeAll();
            Stage(db, uf, R(1, 2, 2));
            db.MergeAll();

            // f(2) = 20 becomes f(1) = 20 at timestamp 3 and collides with f(1) = 10: the merge logs (10, 20).
            db.ApplyRebuild(uf, [f], 3);
            Assert.Contains(Rows(db, log), r => r.SequenceEqual(R(10, 20, 3)));
        }

        [Fact]
        public void ParallelStrataAgreeWithSerialWhenTheUnionFindMergesAlongsideItsWriters()
        {
            // The union-find shares stratum 0 with the constructor tables that write it. With unions of its own pending,
            // it merges in the same parallel stratum while the constructors flush the unions their merges stage into it.
            static (Database Db, TableId Uf, TableId[] Tables) Build(ParallelHeuristics parallelism)
            {
                var db = new Database(parallelism);
                var uf = db.AddTable(new DisplacedTable());
                var tables = new TableId[8];
                for (var t = 0; t < tables.Length; t++)
                    tables[t] = db.AddTable(Function(1, new UnionIdMerge(C(1), uf, C(2)), [C(0), C(1)], parallelism), [], [uf]);

                for (var t = 0; t < tables.Length; t++)
                    Stage(db, tables[t], Enumerable.Range(0, 2000).Select(i => R((uint)i, (uint)(100_000 + t * 10_000 + i), 1)).ToArray());

                db.MergeAll();
                return (db, uf, tables);
            }

            var (serial, serialUf, serialTables) = Build(ParallelHeuristics.Serial);
            for (var round = 0; round < 10; round++)
            {
                var (parallel, parallelUf, parallelTables) = Build(new ParallelHeuristics(8));
                Assert.True(parallel.Parallelism.ParallelizeDbLevelOp(parallel.TotalSizeEstimate));
                if (round == 0)
                {
                    Stage(serial, serialUf, Enumerable.Range(0, 3000).Select(i => R((uint)(100_000 + i), (uint)(110_000 + i), 2)).ToArray());
                    for (var t = 0; t < serialTables.Length; t++)
                        Stage(serial, serialTables[t], Enumerable.Range(0, 2000).Where(i => i % 2 == t % 2).Select(i => R((uint)i, (uint)(50_000 + i), 2)).ToArray());

                    Assert.True(serial.MergeAll());
                }

                Stage(parallel, parallelUf, Enumerable.Range(0, 3000).Select(i => R((uint)(100_000 + i), (uint)(110_000 + i), 2)).ToArray());
                for (var t = 0; t < parallelTables.Length; t++)
                    Stage(parallel, parallelTables[t], Enumerable.Range(0, 2000).Where(i => i % 2 == t % 2).Select(i => R((uint)i, (uint)(50_000 + i), 2)).ToArray());

                Assert.True(parallel.MergeAll());
                var serialFind = (DisplacedTable)serial.GetTable(serialUf);
                var parallelFind = (DisplacedTable)parallel.GetTable(parallelUf);
                Assert.Equal(serialFind.Count, parallelFind.Count);
                for (var id = 50_000u; id < 190_000; id += 3)
                    Assert.Equal(serialFind.FindNaive(id), parallelFind.FindNaive(id));
            }
        }

    }

}

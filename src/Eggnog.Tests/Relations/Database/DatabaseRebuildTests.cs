using System;
using System.Collections.Generic;
using System.Linq;

using Xunit;

using static Eggnog.Relations.Tests.DatabaseTestUtil;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// Tests for <see cref="Database.ApplyRebuild"/>, <see cref="Database.RefreshRowsForValues"/> and the table runner
    /// behind them (egglog's core-relations/src/free_join/mod.rs:365-444), driven as egglog-bridge's rebuild loop
    /// drives them (egglog-bridge/src/lib.rs:626-682).
    /// </summary>
    public class DatabaseRebuildTests
    {

        /// <summary>
        /// A database with the union-find and function tables <c>[a, r, ts]</c> whose two columns are ids, with the
        /// union-id merge, written to the union-find.
        /// </summary>
        static (Database Db, TableId Uf, TableId[] Tables) Setup(int count = 1, ParallelHeuristics? parallelism = null)
        {
            var p = parallelism ?? ParallelHeuristics.Serial;
            var db = new Database(p);
            var uf = db.AddTable(new DisplacedTable(), name: "$uf");
            var tables = new TableId[count];
            for (var i = 0; i < count; i++)
                tables[i] = db.AddTable(Function(1, new UnionIdMerge(C(1), uf, C(2)), [C(0), C(1)], p), [], [uf], "f" + i);

            return (db, uf, tables);
        }

        static uint Find(Database db, TableId uf, uint id) => ((DisplacedTable)db.GetTable(uf)).FindNaive(id);

        /// <summary>
        /// The bridge's native rebuild loop: rebuild every table at the next timestamp, refresh (no dirty containers
        /// here), advance the clock, until nothing changes. Returns the number of passes.
        /// </summary>
        static int RebuildLoop(Database db, TableId uf, TableId[] tables, ref uint ts)
        {
            var passes = 0;
            while (true)
            {
                passes++;
                var rebuilt = db.ApplyRebuild(uf, tables, ts);
                var refreshed = db.RefreshRowsForValues(tables, ReadOnlySpan<uint>.Empty, ts);
                ts++;
                if (rebuilt == false && refreshed == false)
                    return passes;

                Assert.True(passes < 50, "the rebuild loop does not converge");
            }
        }

        [Fact]
        public void ApplyRebuildCanonicalizesAndClosesCongruences()
        {
            var (db, uf, tables) = Setup();
            var f = tables[0];
            Stage(db, f, R(1, 10, 1), R(2, 20, 1), R(3, 30, 1));
            Stage(db, uf, R(1, 2, 2));
            db.MergeAll();

            // f(2) = 20 becomes f(1) = 20, which collides with f(1) = 10: the merge keeps 10 and unions 10 and 20, and
            // the union merges in the same ApplyRebuild.
            Assert.True(db.ApplyRebuild(uf, [f], 3));
            Assert.Equal(new[] { R(1, 10, 1), R(3, 30, 1) }, Rows(db, f));
            Assert.Equal(10u, Find(db, uf, 20));
            Assert.Same(db.Catalog[uf], db.GetTable(uf));

            // Nothing is left to do.
            Assert.False(db.ApplyRebuild(uf, [f], 4));
        }

        [Fact]
        public void TheBridgeRebuildLoopReachesTheCongruenceClosure()
        {
            // f(1) = 10, f(2) = 20, f(10) = 100, f(20) = 200; g(10) = 1000, g(20) = 2000. Unioning 1 and 2 makes 10 =
            // 20, hence 100 = 200 and 1000 = 2000.
            var (db, uf, tables) = Setup(2);
            var (f, g) = (tables[0], tables[1]);
            Stage(db, f, R(1, 10, 1), R(2, 20, 1), R(10, 100, 1), R(20, 200, 1));
            Stage(db, g, R(10, 1000, 1), R(20, 2000, 1));
            db.MergeAll();
            Stage(db, uf, R(1, 2, 2));
            db.MergeAll();

            var ts = 3u;
            var passes = RebuildLoop(db, uf, tables, ref ts);
            Assert.True(passes >= 3);
            Assert.Equal(1u, Find(db, uf, 2));
            Assert.Equal(10u, Find(db, uf, 20));
            Assert.Equal(100u, Find(db, uf, 200));
            Assert.Equal(1000u, Find(db, uf, 2000));
            Assert.Equal(2, db.GetTable(f).Count);
            Assert.Equal(1, db.GetTable(g).Count);
            Assert.True(Rows(db, f).All(r => r[0] == Find(db, uf, r[0]) && r[1] == Find(db, uf, r[1])));
        }

        [Fact]
        public void TheRebuilderIsTakenOutWhileTablesRebuild()
        {
            var (db, uf, tables) = Setup(2);
            var (f, g) = (tables[0], tables[1]);
            var probe = new ProbeOperation([uf, f, g], [g]);
            db.RunWithTakenOut(uf, [f, g], probe);

            var log = probe.Log.ToList();
            Assert.All(log.Where(e => e.Watched == uf), e => Assert.False(e.Visible));

            // Serially each table is out only while it runs.
            Assert.Contains((f, f, false), log);
            Assert.Contains((f, g, true), log);
            Assert.Contains((g, f, true), log);
            Assert.Contains((g, g, false), log);

            // Only the tables the operation reports are notified, and everything is back.
            Assert.Equal(new[] { g }, db.Catalog.Notifications.Reset());
            Assert.Same(db.Catalog[uf], db.GetTable(uf));
            Assert.Same(db.Catalog[f], db.GetTable(f));
        }

        [Fact]
        public void ParallelRunsTakeEveryListedTableOutAtOnce()
        {
            var parallelism = new ParallelHeuristics(4);
            var (db, uf, tables) = Setup(3, parallelism);
            foreach (var table in tables)
                Stage(db, table, Enumerable.Range(0, 4000).Select(i => R((uint)i, (uint)i, 1)).ToArray());

            db.MergeAll();
            Assert.True(parallelism.ParallelizeDbLevelOp(db.TotalSizeEstimate));

            var probe = new ProbeOperation(tables.Append(uf).ToArray(), [tables[0], tables[2]]);
            db.RunWithTakenOut(uf, tables, probe);
            Assert.Equal(12, probe.Log.Count);
            Assert.All(probe.Log, e => Assert.False(e.Visible));
            Assert.Equal(new[] { tables[0], tables[2] }, db.Catalog.Notifications.Reset().OrderBy(t => t.Rep));
            Assert.All(tables.Append(uf), t => Assert.Same(db.Catalog[t], db.GetTable(t)));

            var plain = new ProbeOperation([uf], []);
            db.RunOnTables(tables, plain);
            Assert.All(plain.Log, e => Assert.True(e.Visible));
        }

        [Fact]
        public void InvalidTableListsThrowBeforeAnythingIsTakenOut()
        {
            var (db, uf, tables) = Setup(2);
            Assert.Throws<ArgumentException>(() => db.ApplyRebuild(uf, [tables[0], tables[0]], 1));
            Assert.Throws<ArgumentException>(() => db.ApplyRebuild(uf, [tables[0], uf], 1));
            Assert.Throws<KeyNotFoundException>(() => db.ApplyRebuild(uf, [new TableId(9)], 1));
            Assert.Throws<KeyNotFoundException>(() => db.ApplyRebuild(new TableId(9), tables, 1));
            Assert.Throws<ArgumentException>(() => db.RefreshRowsForValues([tables[1], tables[1]], [1], 1));
            Assert.Throws<ArgumentException>(() => db.RunOnTables([tables[1], tables[1]], new ProbeOperation([], [])));
            Assert.All(tables.Append(uf), t => Assert.Same(db.Catalog[t], db.GetTable(t)));
        }

        [Fact]
        public void AnOperationThatThrowsPropagatesUnwrappedAndPutsEveryTableBack()
        {
            // The serial and the parallel arm throw the same exception: Parallel.For's AggregateException is unwrapped.
            foreach (var parallel in new[] { false, true })
            {
                var parallelism = parallel ? new ParallelHeuristics(4) : ParallelHeuristics.Serial;
                var (db, uf, tables) = Setup(3, parallelism);
                foreach (var table in tables)
                    Stage(db, table, Enumerable.Range(0, 4000).Select(i => R((uint)i, (uint)i, 1)).ToArray());

                db.MergeAll();
                Assert.Equal(parallel, parallelism.ParallelizeDbLevelOp(db.TotalSizeEstimate));

                var e = Assert.Throws<InvalidOperationException>(() => db.RunWithTakenOut(uf, tables, new ThrowingOperation(tables[1])));
                Assert.Contains(tables[1].ToString(), e.Message);
                Assert.Throws<InvalidOperationException>(() => db.RunOnTables(tables, new ThrowingOperation(tables[2])));
                Assert.All(tables.Append(uf), t => Assert.Same(db.Catalog[t], db.GetTable(t)));
                db.Catalog.Notifications.Reset();
            }
        }

        [Fact]
        public void ParallelRebuildsAgreeWithSerialRebuilds()
        {
            static (Database Db, TableId Uf, TableId[] Tables) Build(ParallelHeuristics parallelism)
            {
                var (db, uf, tables) = Setup(4, parallelism);
                for (var t = 0; t < tables.Length; t++)
                    Stage(db, tables[t], Enumerable.Range(0, 4000).Select(i => R((uint)i, (uint)(10_000 + (i * 7 + t) % 5000), 1)).ToArray());

                db.MergeAll();

                // Union every id with its successor modulo 13 among the values, and some keys.
                var unions = new List<uint[]>();
                for (var i = 0u; i < 300; i++)
                    unions.Add(R(10_000 + i, 10_000 + (i + 13) % 300, 2));

                for (var i = 0u; i < 40; i++)
                    unions.Add(R(i, i + 40, 2));

                Stage(db, uf, unions.ToArray());
                db.MergeAll();
                return (db, uf, tables);
            }

            var (serial, serialUf, serialTables) = Build(ParallelHeuristics.Serial);
            var (parallel, parallelUf, parallelTables) = Build(new ParallelHeuristics(4));
            Assert.True(parallel.Parallelism.ParallelizeDbLevelOp(parallel.TotalSizeEstimate));

            var serialTs = 3u;
            var parallelTs = 3u;
            RebuildLoop(serial, serialUf, serialTables, ref serialTs);
            RebuildLoop(parallel, parallelUf, parallelTables, ref parallelTs);

            for (var t = 0; t < serialTables.Length; t++)
            {
                // Timestamps may differ between the runs' passes; compare the canonical rows.
                var a = Rows(serial, serialTables[t]).Select(r => (r[0], r[1])).OrderBy(x => x).ToList();
                var b = Rows(parallel, parallelTables[t]).Select(r => (r[0], r[1])).OrderBy(x => x).ToList();
                Assert.Equal(a, b);
            }

            for (var id = 0u; id < 15_000; id += 3)
                Assert.Equal(Find(serial, serialUf, id), Find(parallel, parallelUf, id));
        }

        [Fact]
        public void RefreshRowsForValuesRestampsRowsMentioningDirtyIds()
        {
            var (db, uf, tables) = Setup(2);
            var (f, g) = (tables[0], tables[1]);
            var plain = db.AddTable(Function(1, KeepCurrentMerge.Instance));
            Stage(db, f, R(1, 10, 1), R(2, 20, 1), R(3, 30, 1));
            Stage(db, g, R(20, 1, 1), R(4, 40, 1));
            Stage(db, plain, R(1, 1, 1));
            db.MergeAll();

            // Without dirty ids nothing runs, not even a merge.
            Stage(db, f, R(9, 90, 1));
            Assert.False(db.RefreshRowsForValues([f, g], ReadOnlySpan<uint>.Empty, 5));
            Assert.Equal(3, db.GetTable(f).Count);

            Assert.True(db.RefreshRowsForValues([f, g, plain], [1, 20], 5));
            Assert.Equal(new[] { R(1, 10, 5), R(2, 20, 5), R(3, 30, 1), R(9, 90, 1) }, Rows(db, f));
            Assert.Equal(new[] { R(4, 40, 1), R(20, 1, 5) }, Rows(db, g));
            Assert.Equal(new[] { R(1, 1, 1) }, Rows(db, plain));
            Assert.Equal(0, db.GetTable(uf).Count);
        }

        [Fact]
        public void ParallelRebuildMergesReadDependenciesFirst()
        {
            // F's merge reads G; both are rebuilt in parallel (the parallel run_on_tables arm) and notified in
            // completion order. G is merged before F reads it.
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
        public void RebuildMergesStampUnionsWithTheRebuildTimestamp()
        {
            // UnionIdMerge without a timestamp column stamps its union with ExecutionState.Timestamp. The MergeAll that
            // ApplyRebuild runs gives its merge states the pass's timestamp, so the union staged by the congruence it
            // finds does not go backwards in the union-find's timestamp column, which the union-find would reject.
            var db = new Database(ParallelHeuristics.Serial);
            var uf = db.AddTable(new DisplacedTable(), name: "$uf");
            var f = db.AddTable(Function(1, new UnionIdMerge(C(1), uf), [C(0), C(1)]), [], [uf], "f");
            Stage(db, f, R(1, 10, 1), R(2, 20, 1));
            db.MergeAll();
            Stage(db, uf, R(1, 2, 2));
            db.MergeAll();

            // f(2) = 20 becomes f(1) = 20, which collides with f(1) = 10 and unions 10 and 20 at the rebuild's
            // timestamp.
            db.ApplyRebuild(uf, [f], 3);
            Assert.Equal(10u, ((DisplacedTable)db.GetTable(uf)).FindNaive(20));
            Assert.Null(db.Failure);
        }

        [Fact]
        public void RowsMergeFunctionsWriteDuringARebuildCarryThePassTimestamp()
        {
            // Every write of a rebuild pass, including writes made by merge functions, carries the pass's captured
            // timestamp. A merge state carrying 0 would break the sort order of a table sorted by timestamp once that
            // table holds later rows.
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

    }

}

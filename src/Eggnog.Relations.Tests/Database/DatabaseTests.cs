using System;
using System.Collections.Generic;
using System.Linq;

using Xunit;

using static Eggnog.Relations.Tests.DatabaseTestUtil;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// Tests for the catalog side of <see cref="Database"/>: adding and finding tables, buffers and notification,
    /// counters, external functions, size estimates, clearing, execution states, failures and cloning. The data
    /// loading of egglog's core-relations/src/tests.rs (<c>basic_math_egraph</c>, <c>basic_query</c>,
    /// <c>minimal_ac</c>) is ported up to the point where those tests start running rules.
    /// </summary>
    public class DatabaseTests
    {

        /// <summary>
        /// egglog's <c>basic_math_egraph</c> (core-relations/src/tests.rs:961-1010): the union-find, <c>num(i, id, ts)</c>
        /// and <c>add(x, y, id, ts)</c>, both reading the union-find, with the "take the new id and union" merge.
        /// </summary>
        internal static (Database Db, TableId Uf, TableId Num, TableId Add, CounterId Ids) BasicMathEgraph(ParallelHeuristics? parallelism = null)
        {
            var db = new Database(parallelism ?? ParallelHeuristics.Serial);
            var uf = db.AddTable(new DisplacedTable());
            var num = db.AddTable(new SortedWritesTable(1, 3, C(2), [], new UnionTakeNewMerge(uf, 1, 2), parallelism ?? ParallelHeuristics.Serial), [uf]);
            var ids = db.AddCounter();
            var add = db.AddTable(new SortedWritesTable(2, 4, C(3), [], new UnionTakeNewMerge(uf, 2, 3), parallelism ?? ParallelHeuristics.Serial), [uf]);
            return (db, uf, num, add, ids);
        }

        [Fact]
        public void BasicQueryDataLoads()
        {
            // egglog's basic_query_inner (tests.rs:45-90), up to the rule: numbers 0..10 with fresh ids, then (add x y).
            var (db, _, num, add, idCounter) = BasicMathEgraph();
            var ids = new List<uint>();
            var numBuffer = db.NewBuffer(num);
            for (var i = 0u; i < 10; i++)
            {
                var id = (uint)db.IncrementCounter(idCounter);
                ids.Add(i);
                numBuffer.StageInsert([i, id, 0]);
            }

            numBuffer.Flush();
            Assert.True(db.MergeAll());

            var addIds = new List<uint>();
            var addBuffer = db.NewBuffer(add);
            for (var i = 0; i < ids.Count; i += 2)
            {
                var id = (uint)db.IncrementCounter(idCounter);
                addIds.Add(id);
                addBuffer.StageInsert([ids[i], ids[i + 1], id, 0]);
            }

            addBuffer.Flush();
            Assert.True(db.MergeAll());

            Assert.Equal(15, db.ReadCounter(idCounter));
            Assert.Equal(Enumerable.Range(0, 10).Select(i => R((uint)i, (uint)i, 0)), Rows(db, num));
            Assert.Equal(new[] { R(0, 1, 10, 0), R(2, 3, 11, 0), R(4, 5, 12, 0), R(6, 7, 13, 0), R(8, 9, 14, 0) }, Rows(db, add));
        }

        [Fact]
        public void MinimalAcDataLoads()
        {
            // egglog's minimal_ac_inner (tests.rs:392-414), up to the rule; the expected rows are the five inputs.
            var (db, _, _, add, _) = BasicMathEgraph();
            Stage(db, add, R(0, 0, 1, 0), R(0, 1, 2, 0), R(0, 2, 3, 0));
            db.MergeAll();
            Stage(db, add, R(1, 0, 2, 1), R(1, 1, 3, 1));
            db.MergeAll();

            Assert.Equal(
                new[] { R(0, 0, 1, 0), R(0, 1, 2, 0), R(0, 2, 3, 0), R(1, 0, 2, 1), R(1, 1, 3, 1) },
                Rows(db, add));
        }

        [Fact]
        public void TheMathMergeUnionsConflictingIds()
        {
            var (db, uf, num, _, _) = BasicMathEgraph();
            Stage(db, num, R(1, 10, 0), R(2, 20, 0));
            db.MergeAll();
            Stage(db, num, R(1, 11, 1));

            // The num merge takes id 11 and stages 10 = 11 into the union-find, which the same MergeAll merges.
            Assert.True(db.MergeAll());
            Assert.Equal(new[] { R(1, 11, 1), R(2, 20, 0) }, Rows(db, num));
            Assert.Equal(1, db.GetTable(uf).Count);
            Assert.Equal(10u, ((DisplacedTable)db.GetTable(uf)).FindNaive(11));
        }

        [Fact]
        public void AddTableAssignsIdsNamesAndStrata()
        {
            var db = new Database(ParallelHeuristics.Serial);
            Assert.Equal(new TableId(0), db.NextTableId);
            var uf = db.AddTable(new DisplacedTable(), name: "$uf");
            var f = Function(1, KeepCurrentMerge.Instance);
            Assert.Equal(new TableId(1), db.NextTableId);
            var fId = db.AddTable(f, [uf], [uf], "f");

            Assert.Equal(new TableId(0), uf);
            Assert.Equal(new TableId(1), fId);
            Assert.Equal(2, db.TableCount);
            Assert.Same(f, db.GetTable(fId));
            Assert.Equal("f", db.GetTableInfo(fId).Name);
            Assert.Equal("$uf", db.ReadOnlyView.GetTableName(uf));
            Assert.Same(f.Spec, db.GetTableInfo(fId).Spec);
            Assert.Equal(1, db.Dependencies.LevelOf(fId));
            Assert.Equal(new[] { uf }, db.Dependencies.WriteDependencies(fId).ToArray());
            Assert.Same(f, db.Catalog[fId]);
            Assert.Equal(new[] { uf, fId }, db.ReadOnlyView.TableIds);
        }

        [Fact]
        public void InvalidAddsThrowAndLeaveTheDatabaseUnchanged()
        {
            var db = new Database(ParallelHeuristics.Serial);
            var f = Function(1, KeepCurrentMerge.Instance);
            var fId = db.AddTable(f);

            Assert.Throws<ArgumentException>(() => db.AddTable(f));
            Assert.Throws<ArgumentException>(() => db.AddTable(Function(1, KeepCurrentMerge.Instance), [new TableId(5)]));
            Assert.Throws<ArgumentNullException>(() => db.AddTable(null!));
            Assert.Equal(1, db.TableCount);
            Assert.Equal(1, db.Catalog.Count);

            var g = db.AddTable(Function(1, KeepCurrentMerge.Instance), [fId]);
            Assert.Equal(new TableId(1), g);
            Assert.Same(db.Catalog[g], db.GetTable(g));
        }

        [Fact]
        public void UnknownTablesThrow()
        {
            var db = new Database(ParallelHeuristics.Serial);
            var e = Assert.Throws<KeyNotFoundException>(() => db.GetTable(new TableId(3)));
            Assert.Contains("not been declared", e.Message);
            Assert.False(db.TryGetTableInfo(new TableId(3), out _));
            Assert.Throws<KeyNotFoundException>(() => db.NewBuffer(new TableId(3)));

            // A bad id leaves no notification behind that the next merge would trip over.
            Assert.False(db.MergeAll());
        }

        [Fact]
        public void BuffersFromTheDatabaseNotifyButTableBuffersDoNot()
        {
            var db = new Database(ParallelHeuristics.Serial);
            var f = db.AddTable(Function(1, KeepCurrentMerge.Instance));

            // Staged through the table's own buffer: not notified, so MergeAll does not merge it (egglog's warning on
            // get_table), but MergeTable does.
            var raw = db.GetTable(f).NewBuffer();
            raw.StageInsert([1, 1, 0]);
            raw.Flush();
            Assert.False(db.MergeAll());
            Assert.Empty(Rows(db, f));
            Assert.True(db.MergeTable(f));
            Assert.Single(Rows(db, f));

            // An unflushed database buffer has notified, but its rows arrive only once flushed.
            var buffer = db.NewBuffer(f);
            buffer.StageInsert([2, 2, 0]);
            Assert.False(db.MergeAll());
            buffer.Flush();
            Assert.False(db.MergeAll());
            Assert.Single(Rows(db, f));
            Assert.True(db.MergeTable(f));
            Assert.Equal(2, db.GetTable(f).Count);
        }

        [Fact]
        public void CountersAreSharedByTheDatabaseTheViewAndStates()
        {
            var db = new Database(ParallelHeuristics.Serial);
            var a = db.AddCounter();
            var b = db.AddCounter();
            Assert.Equal(0, db.IncrementCounter(a));
            Assert.Equal(1, db.ReadOnlyView.IncrementCounter(a));
            var state = db.CreateExecutionState();
            Assert.Equal(2, state.IncrementCounter(a));
            Assert.Equal(0, state.Clone().IncrementCounter(b));
            Assert.Equal(3, db.ReadCounter(a));
            Assert.Equal(1, db.ReadOnlyView.ReadCounter(b));
            Assert.Equal(1, state.ReadCounter(b));
            Assert.Throws<KeyNotFoundException>(() => db.IncrementCounter(new CounterId(2)));
        }

        [Fact]
        public void ExternalFunctionsRunWithTheCallingState()
        {
            // As in egglog's lookup_with_fallback / call_external_with_fallback tests: functions that return a value,
            // and functions that decline (assert_even).
            var db = new Database(ParallelHeuristics.Serial);
            var log = db.AddTable(Function(1, KeepCurrentMerge.Instance));
            var counter = db.AddCounter();
            var assertEven = db.AddExternalFunction(ExternalFunction.Create((ExecutionState _, ReadOnlySpan<uint> args, out uint result) =>
            {
                result = args[0];
                return args[0] % 2 == 0;
            }));
            var record = db.AddExternalFunction(ExternalFunction.Create((ExecutionState state, ReadOnlySpan<uint> args, out uint result) =>
            {
                result = (uint)state.IncrementCounter(counter);
                state.StageInsert(log, [args[0], result, 0]);
                return true;
            }));

            var state = db.CreateExecutionState();
            Assert.True(state.TryCallExternalFunction(assertEven, [4], out var even));
            Assert.Equal(4u, even);
            Assert.False(state.TryCallExternalFunction(assertEven, [3], out _));
            Assert.True(state.TryCallExternalFunction(record, [9], out var first));
            Assert.True(state.Clone().TryCallExternalFunction(record, [8], out var second));
            Assert.Equal(0u, first);
            Assert.Equal(1u, second);
            Assert.True(state.Changed);

            // Staged writes become visible after flushing and merging.
            state.Flush();
            Assert.True(db.MergeAll());
            Assert.Equal(new[] { R(9, 0, 0) }, Rows(db, log));

            // A freed id is reused; the old function is gone.
            db.FreeExternalFunction(assertEven);
            Assert.Throws<KeyNotFoundException>(() => db.GetExternalFunction(assertEven));
            Assert.Throws<KeyNotFoundException>(() => state.TryCallExternalFunction(assertEven, [2], out _));
            var replacement = ExternalFunction.Create((ExecutionState _, ReadOnlySpan<uint> _, out uint result) =>
            {
                result = 42;
                return true;
            });
            Assert.Equal(assertEven, db.AddExternalFunction(replacement));
            Assert.Same(replacement, db.ReadOnlyView.GetExternalFunction(assertEven));
            db.FreeExternalFunction(new ExternalFunctionId(17));
        }

        [Fact]
        public void EstimateSizeUsesFastSubsetsWhenItCan()
        {
            var db = new Database(ParallelHeuristics.Serial);
            var f = db.AddTable(Function(1, ClosureMerge.TakeNew));
            Stage(db, f, R(1, 10, 0), R(2, 20, 0));
            db.MergeAll();
            Stage(db, f, R(3, 30, 1), R(4, 40, 1));
            db.MergeAll();
            StageRemove(db, f, R(3));
            db.MergeAll();

            Assert.Equal(3, db.EstimateSize(f));

            // The timestamp column is sorted, so a range on it is fast and counts the tombstone of row 3.
            Assert.Equal(2, db.EstimateSize(f, Constraint.GeConst(C(2), 1)));
            Assert.Equal(2, db.EstimateSize(f, Constraint.LtConst(C(2), 1)));

            // Other columns are refined exactly over the live rows.
            Assert.Equal(1, db.EstimateSize(f, Constraint.GtConst(C(1), 20)));
            Assert.Equal(0, db.EstimateSize(f, Constraint.EqConst(C(0), 3)));
            Assert.Equal(1, db.EstimateSize(f, Constraint.EqConst(C(1), 40)));
            Assert.Equal(3, db.EstimateSize(f, Constraint.Eq(C(0), C(0))));
        }

        [Fact]
        public void ClearTableEmptiesTheTableAndResetsItsIndexes()
        {
            var db = new Database(ParallelHeuristics.Serial);
            var f = db.AddTable(Function(1, ClosureMerge.TakeNew));
            Stage(db, f, R(1, 10, 0), R(2, 20, 0));
            db.MergeAll();
            Assert.Equal(2, db.TotalSizeEstimate);
            using (var processed = db.ProcessConstraints(f, [Constraint.EqConst(C(1), 10)]))
                Assert.Equal(1, processed.ApproximateSize);

            // Staged writes are dropped too.
            Stage(db, f, R(3, 30, 1));
            db.ClearTable(f);
            Assert.Equal(0, db.GetTable(f).Count);
            Assert.Equal(0, db.TotalSizeEstimate);
            Assert.False(db.MergeAll());
            Assert.Empty(Rows(db, f));

            using (var processed = db.ProcessConstraints(f, [Constraint.EqConst(C(1), 10)]))
                Assert.Equal(0, processed.ApproximateSize);

            Stage(db, f, R(5, 10, 2));
            db.MergeAll();
            using (var processed = db.ProcessConstraints(f, [Constraint.EqConst(C(1), 10)]))
                Assert.Equal(new[] { R(5, 10, 2) }, Rows(db.GetTable(f), processed.Rows));
        }

        [Fact]
        public void WithExecutionStateFlushesAndNotifies()
        {
            var db = new Database(ParallelHeuristics.Serial);
            var f = db.AddTable(Function(1, KeepCurrentMerge.Instance));
            var result = db.WithExecutionState(state =>
            {
                Assert.Same(db.ReadOnlyView, state.View);
                Assert.Equal(7u, state.Timestamp);
                state.StageInsert(f, [1, 2, 7]);
                return 5;
            }, 7);

            Assert.Equal(5, result);
            Assert.True(db.MergeAll());
            Assert.Equal(new[] { R(1, 2, 7) }, Rows(db, f));
            Assert.Same(db.ReadOnlyView, db.CreateExecutionState(3).View);
            Assert.Equal(3u, db.CreateExecutionState(3).Timestamp);
        }

        [Fact]
        public void MergeFailuresAreCollectedUntilTaken()
        {
            var db = new Database(ParallelHeuristics.Serial);
            var f = db.AddTable(Function(1, FailingMerge.Instance));
            Stage(db, f, R(1, 10, 0));
            db.MergeAll();
            Assert.Null(db.Failure);

            Stage(db, f, R(1, 11, 1));
            Stage(db, f, R(1, 12, 1));
            Assert.False(db.MergeAll());
            Assert.Equal("conflict: [1, 10, 0] vs [1, 11, 1]", db.Failure);
            Assert.Equal("conflict: [1, 10, 0] vs [1, 11, 1]", db.TakeFailure());
            Assert.Null(db.Failure);
            Assert.Null(db.TakeFailure());

            db.WithExecutionState(state =>
            {
                state.ReportFailure("from a state");
                return 0;
            });
            Assert.Equal("from a state", db.TakeFailure());
        }

        [Fact]
        public void ClonesAreDeepAndCarryStagedWrites()
        {
            var (db, uf, num, _, ids) = BasicMathEgraph();
            var function = ExternalFunction.Create((ExecutionState _, ReadOnlySpan<uint> _, out uint result) =>
            {
                result = 1;
                return true;
            });
            var fn = db.AddExternalFunction(function);
            Stage(db, num, R(1, 10, 0), R(2, 20, 0));
            db.MergeAll();
            db.IncrementCounter(ids);
            using (db.ProcessConstraints(num, [Constraint.EqConst(C(1), 20)]))
            {
            }

            // A write staged but not merged travels with the clone.
            Stage(db, num, R(3, 30, 1));
            var clone = db.Clone();
            Assert.NotSame(db.GetTable(num), clone.GetTable(num));
            Assert.Same(clone.GetTable(num), clone.Catalog[num]);
            Assert.Same(function, clone.GetExternalFunction(fn));
            Assert.Equal(1, clone.ReadCounter(ids));
            Assert.Equal(1, clone.GetTableInfo(num).ColumnIndexes.Count);
            Assert.Equal(1, clone.Dependencies.LevelOf(num));
            Assert.Equal(db.TotalSizeEstimate, clone.TotalSizeEstimate);

            Assert.True(clone.MergeAll());
            Assert.Equal(3, clone.GetTable(num).Count);
            Assert.True(db.MergeAll());
            Assert.Equal(3, db.GetTable(num).Count);

            // From here on the two diverge.
            Stage(clone, num, R(1, 11, 2));
            clone.MergeAll();
            clone.IncrementCounter(ids);
            Assert.Equal(1, clone.GetTable(uf).Count);
            Assert.Equal(0, db.GetTable(uf).Count);
            Assert.Equal(1, db.ReadCounter(ids));
            Assert.Equal(2, clone.ReadCounter(ids));
            using (var processed = clone.ProcessConstraints(num, [Constraint.EqConst(C(1), 11)]))
                Assert.Equal(1, processed.ApproximateSize);
            using (var processed = db.ProcessConstraints(num, [Constraint.EqConst(C(1), 11)]))
                Assert.Equal(0, processed.ApproximateSize);

            // Each clone's states find their own view, clones included.
            Assert.Same(clone.ReadOnlyView, clone.CreateExecutionState().Clone().View);
            Assert.Same(db.ReadOnlyView, db.CreateExecutionState().Clone().View);
        }

    }

}

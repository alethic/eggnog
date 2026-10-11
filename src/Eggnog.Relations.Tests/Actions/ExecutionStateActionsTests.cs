using System;
using System.Collections.Generic;
using System.Linq;

using Xunit;

using static Eggnog.Relations.Tests.ActionTestUtil;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// Tests for the action-facing parts of <see cref="ExecutionState"/>: the early-stop tests of egglog's
    /// core-relations/src/action/tests.rs, and the predicted-value API (<c>predict_val</c>, <c>predict_col</c>), the
    /// batch calls of external functions and <c>table_ids</c>.
    /// </summary>
    public class ExecutionStateActionsTests
    {

        /// <summary>
        /// egglog's <c>empty_execution_state!</c>: a state over the view of an empty database.
        /// </summary>
        static ExecutionState EmptyState() => new(new Database().ReadOnlyView);

        /// <summary>
        /// egglog's <c>test_early_stop_initial_state</c>.
        /// </summary>
        [Fact]
        public void EarlyStopInitialState()
        {
            var state = EmptyState();
            Assert.False(state.ShouldStop);
        }

        /// <summary>
        /// egglog's <c>test_early_stop_trigger</c>.
        /// </summary>
        [Fact]
        public void EarlyStopTrigger()
        {
            var state = EmptyState();
            Assert.False(state.ShouldStop);
            state.TriggerEarlyStop();
            Assert.True(state.ShouldStop);
        }

        /// <summary>
        /// egglog's <c>test_early_stop_shared_across_clones</c>.
        /// </summary>
        [Fact]
        public void EarlyStopSharedAcrossClones()
        {
            var state1 = EmptyState();
            var state2 = state1.Clone();
            Assert.False(state1.ShouldStop);
            Assert.False(state2.ShouldStop);

            state1.TriggerEarlyStop();
            Assert.True(state1.ShouldStop);
            Assert.True(state2.ShouldStop);
        }

        /// <summary>
        /// egglog's <c>test_early_stop_multiple_clones</c>.
        /// </summary>
        [Fact]
        public void EarlyStopMultipleClones()
        {
            var state1 = EmptyState();
            var state2 = state1.Clone();
            var state3 = state2.Clone();
            Assert.False(state1.ShouldStop);
            Assert.False(state2.ShouldStop);
            Assert.False(state3.ShouldStop);

            state2.TriggerEarlyStop();
            Assert.True(state1.ShouldStop);
            Assert.True(state2.ShouldStop);
            Assert.True(state3.ShouldStop);
        }

        [Fact]
        public void TheInterpreterDoesNotCheckTheStopFlag()
        {
            var db = new Database(ParallelHeuristics.Serial);
            var t = db.AddTable(Plain(1, 1));
            var state = db.CreateExecutionState();
            state.TriggerEarlyStop();
            Assert.Equal(2, state.RunInstrs([new InsertInstr(t, [V(0)])], Batch((0, [1, 2]))));
            Assert.True(state.Changed);
        }

        [Fact]
        public void PredictRowReturnsTheCommittedRowOrStagesANewOne()
        {
            var db = new Database(ParallelHeuristics.Serial);
            var f = db.AddTable(Plain(1, 3));
            var ids = db.AddCounter();
            Fill(db, f, [1, 10, 11]);

            var state = db.CreateExecutionState();
            Assert.Equal(new uint[] { 1, 10, 11 }, state.PredictRow(f, [1], [ids, new Value(5)]));
            Assert.False(state.Changed);
            Assert.Equal(0, db.ReadCounter(ids));

            Assert.Equal(new uint[] { 2, 0, 5 }, state.PredictRow(f, [2], [ids, new Value(5)]));
            Assert.True(state.Changed);
            Assert.Equal(1, state.PredictedCount);

            // The prediction is stable, and the column form reads the same row.
            Assert.Equal(new uint[] { 2, 0, 5 }, state.PredictRow(f, [2], [ids, new Value(6)]));
            Assert.Equal(0u, state.PredictColumn(f, [2], [ids, new Value(6)], C(1)));
            Assert.Equal(1, db.ReadCounter(ids));

            // A new key through the column form.
            Assert.Equal(1u, state.PredictColumn(f, [3], [ids, new Value(7)], C(1)));
            Assert.Equal(7u, state.PredictColumn(f, new uint[] { 9, 3 }, 1, 1, [ids, new Value(8)], C(2)));
            Assert.Equal(10u, state.PredictColumn(f, new uint[] { 1 }, 0, 1, [ids, new Value(8)], C(1)));

            Span<uint> row = stackalloc uint[3];
            state.PredictRow(f, [3], [ids, new Value(7)], row);
            Assert.Equal(new uint[] { 3, 1, 7 }, row.ToArray());
            Assert.Throws<ArgumentException>(() => state.PredictRow(f, [3], [ids, new Value(7)], new uint[2]));
            Assert.Throws<ArgumentOutOfRangeException>(() => state.PredictColumn(f, [4], [ids, new Value(1)], C(3)));

            // A row of the wrong width is rejected before an id is minted.
            Assert.Throws<ArgumentException>(() => state.PredictColumn(f, [4], [ids], C(2)));
            Assert.Throws<ArgumentException>(() => state.PredictRow(f, [4], [ids, ids, ids]));
            Assert.Equal(2, db.ReadCounter(ids));

            Assert.True(Commit(db, state));
            Assert.Equal("[1, 10, 11] [2, 0, 5] [3, 1, 7]", DatabaseTestUtil.Dump(db, f));
        }

        [Fact]
        public void PredictionsAreSharedWithLookupOrInsertDefault()
        {
            var db = new Database(ParallelHeuristics.Serial);
            var f = db.AddTable(Plain(1, 2));
            var ids = db.AddCounter();
            var state = db.CreateExecutionState();
            Assert.Equal(new uint[] { 4, 0 }, state.PredictRow(f, [4], [ids]));

            var b = Batch((0, [4, 5]));
            state.RunInstrs([new LookupOrInsertDefaultInstr(f, [V(0)], [ids], C(1), V(1))], b);
            Assert.Equal(new uint[] { 0, 1 }, Column(b, 1));
            Assert.Equal(1u, state.PredictColumn(f, [5], [ids], C(1)));
            Assert.Equal(2, state.PredictedCount);
            Assert.Equal(2, db.ReadCounter(ids));
        }

        [Fact]
        public void PredictionsAreKeptPerTable()
        {
            var db = new Database(ParallelHeuristics.Serial);
            var f = db.AddTable(Plain(1, 2));
            var g = db.AddTable(Plain(1, 2));
            var ids = db.AddCounter();
            var state = db.CreateExecutionState();
            Assert.Equal(0u, state.PredictColumn(f, [4], [ids], C(1)));
            Assert.Equal(1u, state.PredictColumn(g, [4], [ids], C(1)));
            Assert.Equal(0u, state.PredictColumn(f, [4], [ids], C(1)));
        }

        [Fact]
        public void PredictingIntoATableTheDatabaseTookOutThrows()
        {
            var db = new Database(ParallelHeuristics.Serial);
            var ids = db.AddCounter();
            var self = db.NextTableId;
            Exception? caught = null;
            var f = db.AddTable(Plain(1, 2, new PredictingMerge(self, ids, e => caught = e)));
            Fill(db, f, [1, 1]);
            Fill(db, f, [1, 2]);
            Assert.IsType<KeyNotFoundException>(caught);
        }

        [Fact]
        public void BatchCallsFillOrAssignTheOutputColumn()
        {
            var db = new Database(ParallelHeuristics.Serial);
            var state = db.CreateExecutionState();
            var half = Function((_, a) => a[0] % 2 == 0 ? a[0] / 2 : null);
            var b = Batch((0, [2, 3, 4, 5]));

            var mask = new Mask(4);
            state.InvokeBatch(half, ref mask, b, [V(0)], V(1));
            Assert.Equal(new[] { 0, 2 }, Lanes(mask));
            Assert.Equal(new uint[] { 1, S, 2, S }, Column(b, 1));

            var all = new Mask(4);
            var plusOne = Function((_, a) => a[0] == 4 ? null : a[0] + 1);
            state.InvokeBatchAssign(plusOne, ref all, b, [V(0)], V(1));
            Assert.Equal(new[] { 0, 1, 3 }, Lanes(all));
            Assert.Equal(new uint[] { 3, 4, 2, 6 }, Column(b, 1));

            var m = new Mask(4);
            Assert.Throws<InvalidOperationException>(() => state.InvokeBatchAssign(plusOne, ref m, b, [V(0)], V(9)));
            Assert.Throws<ArgumentException>(() =>
            {
                var tooLong = new Mask(10);
                state.InvokeBatch(half, ref tooLong, new Bindings(4), [], V(1));
            });
        }

        [Fact]
        public void ExternalFunctionsMayCallBackIntoTheInterpreter()
        {
            var db = new Database(ParallelHeuristics.Serial);
            var t = db.AddTable(Plain(1, 1));
            var inner = Function((_, a) => a[0] + a[1] + a[2] + a[3] + a[4]);
            var innerId = db.AddExternalFunction(inner);
            var outer = db.AddExternalFunction(Function((s, a) =>
            {
                // A nested batch on the same thread rents its own scratch arrays.
                var nested = Batch((0, [a[0]]), (1, [a[1]]));
                var n = s.RunInstrs([new ExternalInstr(innerId, [V(0), V(1), V(0), V(1), K(1)], V(2)), new InsertInstr(t, [V(2)])], nested);
                return n == 1 ? nested[V(2)][0] : null;
            }));

            var state = db.CreateExecutionState();
            var b = Batch((0, [1, 2]), (1, [10, 20]), (2, [7, 7]));
            Assert.Equal(2, state.RunInstrs([new ExternalInstr(outer, [V(0), V(1), V(2), V(2), V(2)], V(3))], b));
            Assert.Equal(new uint[] { 23, 45 }, Column(b, 3));
            Commit(db, state);
            Assert.Equal("[23] [45]", DatabaseTestUtil.Dump(db, t));
        }

        [Fact]
        public void TableIdsListTheVisibleTables()
        {
            var db = new Database(ParallelHeuristics.Serial);
            var f = db.AddTable(Plain(1, 2));
            var g = db.AddTable(Plain(1, 2));
            Assert.Equal(new[] { f, g }, db.CreateExecutionState().TableIds.ToArray());
            Assert.Throws<InvalidOperationException>(() => TableTestUtil.EmptyState().TableIds.ToArray());
        }

        static List<int> Lanes(Mask mask)
        {
            var lanes = new List<int>();
            foreach (var lane in mask)
                lanes.Add(lane);

            return lanes;
        }

    }

}

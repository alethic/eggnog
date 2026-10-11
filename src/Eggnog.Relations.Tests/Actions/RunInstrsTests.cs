using System;
using System.Collections.Generic;
using System.Linq;

using Xunit;

using static Eggnog.Relations.Tests.ActionTestUtil;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// Tests for the vectorized interpreter, <see cref="ExecutionState.RunInstrs(ReadOnlySpan{Instr}, Bindings)"/>: the
    /// semantics of every instruction of egglog's <c>run_instr</c> (core-relations/src/action/mod.rs:598-882).
    /// </summary>
    public class RunInstrsTests
    {

        static string Dump(Database db, TableId table) => DatabaseTestUtil.Dump(db, table);

        /// <summary>
        /// A database with <c>f(k) = v</c> holding (1, 10) and (2, 20).
        /// </summary>
        static (Database Db, TableId F) OneTable()
        {
            var db = new Database(ParallelHeuristics.Serial);
            var f = db.AddTable(Plain(1, 2), name: "f");
            Fill(db, f, [1, 10], [2, 20]);
            return (db, f);
        }

        [Fact]
        public void LookupBindsTheColumnAndDropsMissingKeys()
        {
            var (db, f) = OneTable();
            var state = db.CreateExecutionState();
            var b = Batch((0, [1, 2, 3, 1]));
            var n = state.RunInstrs([new LookupInstr(f, [V(0)], C(1), V(1))], b);
            Assert.Equal(3, n);
            Assert.Equal(new uint[] { 10, 20, S, 10 }, Column(b, 1));
            Assert.False(state.Changed);
        }

        [Fact]
        public void LookupWithAConstantKey()
        {
            var (db, f) = OneTable();
            var state = db.CreateExecutionState();
            var b = Batch((0, [7, 8]));
            Assert.Equal(2, state.RunInstrs([new LookupInstr(f, [K(2)], C(1), V(1))], b));
            Assert.Equal(new uint[] { 20, 20 }, Column(b, 1));
            Assert.Equal(0, state.RunInstrs([new LookupInstr(f, [K(5)], C(1), V(2))], b));
        }

        [Fact]
        public void LookupWithDefaultNeverDropsALane()
        {
            var (db, f) = OneTable();
            var state = db.CreateExecutionState();
            var b = Batch((0, [1, 3, 2, 4]), (1, [100, 101, 102, 103]));
            Assert.Equal(4, state.RunInstrs([new LookupWithDefaultInstr(f, [V(0)], C(1), V(2), V(1))], b));
            Assert.Equal(new uint[] { 10, 101, 20, 103 }, Column(b, 2));
            Assert.Equal(4, state.RunInstrs([new LookupWithDefaultInstr(f, [V(0)], C(1), V(3), K(9))], b));
            Assert.Equal(new uint[] { 10, 9, 20, 9 }, Column(b, 3));
        }

        [Fact]
        public void LookupOrInsertDefaultMintsOneIdPerMissingKeyAndSetsChanged()
        {
            var (db, f) = OneTable();
            var ids = db.AddCounter();
            for (var i = 0; i < 50; i++)
                db.IncrementCounter(ids);

            var state = db.CreateExecutionState();
            var b = Batch((0, [1, 5, 5, 2, 6]));
            var n = state.RunInstrs([new LookupOrInsertDefaultInstr(f, [V(0)], [ids], C(1), V(1))], b);
            Assert.Equal(5, n);

            // Both lanes with key 5 get the same fresh id; the ids follow lane order.
            Assert.Equal(new uint[] { 10, 50, 50, 20, 51 }, Column(b, 1));
            Assert.Equal(52, db.ReadCounter(ids));
            Assert.Equal(2, state.PredictedCount);

            // egglog does not set `changed` here (core-relations/src/action/mod.rs:707); the port does.
            Assert.True(state.Changed);
            Assert.Contains(f, db.Catalog.Notifications.Reset());
            db.Catalog.Notifications.Notify(f);

            // Rows staged by this state are not visible to lookups until merged.
            var b2 = Batch((0, [5]));
            Assert.Equal(0, state.RunInstrs([new LookupInstr(f, [V(0)], C(1), V(1))], b2));

            Assert.True(Commit(db, state));
            Assert.Equal("[1, 10] [2, 20] [5, 50] [6, 51]", Dump(db, f));
        }

        [Fact]
        public void PredictionsLastForTheStateAndClonesStartEmpty()
        {
            var (db, f) = OneTable();
            var ids = db.AddCounter();
            var state = db.CreateExecutionState();
            Instr[] program = [new LookupOrInsertDefaultInstr(f, [V(0)], [ids], C(1), V(1))];

            var b = Batch((0, [7]));
            state.RunInstrs(program, b);
            Assert.Equal(new uint[] { 0 }, Column(b, 1));

            // A second batch through the same state sees the same prediction and stages nothing new.
            state.Changed = false;
            var again = Batch((0, [7, 8]));
            state.RunInstrs(program, again);
            Assert.Equal(new uint[] { 0, 1 }, Column(again, 1));
            Assert.True(state.Changed);

            state.Changed = false;
            var same = Batch((0, [7]));
            state.RunInstrs(program, same);
            Assert.Equal(new uint[] { 0 }, Column(same, 1));
            Assert.False(state.Changed);

            // A clone has an empty cache, so it mints another id for the same key, as egglog's per-batch clones do.
            var clone = state.Clone();
            Assert.Equal(0, clone.PredictedCount);
            var other = Batch((0, [7]));
            clone.RunInstrs(program, other);
            Assert.Equal(new uint[] { 2 }, Column(other, 1));

            state.Flush();
            clone.Flush();
            db.MergeAll();

            // TakeNew merges keep the last staged row for key 7.
            Assert.Equal(new uint[] { 7, 8 }, DatabaseTestUtil.Rows(db, f).Where(r => r[0] > 2).Select(r => r[0]));
        }

        [Fact]
        public void LookupOrInsertDefaultEvaluatesEveryKindOfDefault()
        {
            var db = new Database(ParallelHeuristics.Serial);
            var g = db.AddTable(Plain(2, 6));
            var ids = db.AddCounter();
            var state = db.CreateExecutionState();
            var b = Batch((0, [1, 2]), (1, [3, 4]), (2, [70, 80]));
            WriteVal[] defaults = [ids, V(2), new Value(9), WriteVal.CurrentValue(1)];
            Assert.Equal(2, state.RunInstrs([new LookupOrInsertDefaultInstr(g, [V(0), V(1)], defaults, C(2), V(3))], b));
            Assert.Equal(new uint[] { 0, 1 }, Column(b, 3));

            // The destination may be any column of the row, the key included.
            Assert.Equal(2, state.RunInstrs([new LookupOrInsertDefaultInstr(g, [V(0), V(1)], defaults, C(5), V(4))], b));
            Assert.Equal(new uint[] { 3, 4 }, Column(b, 4));
            Assert.Equal(2, db.ReadCounter(ids));

            Commit(db, state);
            Assert.Equal("[1, 3, 0, 70, 9, 3] [2, 4, 1, 80, 9, 4]", Dump(db, g));
        }

        [Fact]
        public void LookupOrInsertDefaultOnlyBuildsRowsForLiveMisses()
        {
            var (db, f) = OneTable();
            var ids = db.AddCounter();
            var state = db.CreateExecutionState();
            var b = Batch((0, [1, 5, 6, 7]));
            Instr[] program =
            [
                new AssertNeInstr(V(0), K(6)),
                new LookupOrInsertDefaultInstr(f, [V(0)], [ids], C(1), V(1)),
            ];
            Assert.Equal(3, state.RunInstrs(program, b));
            Assert.Equal(new uint[] { 10, 0, S, 1 }, Column(b, 1));
            Assert.Equal(2, db.ReadCounter(ids));
        }

        [Fact]
        public void LookupOrInsertDefaultWithoutMissesStagesNothing()
        {
            var (db, f) = OneTable();
            var ids = db.AddCounter();
            var state = db.CreateExecutionState();
            var b = Batch((0, [1, 2]));
            Assert.Equal(2, state.RunInstrs([new LookupOrInsertDefaultInstr(f, [V(0)], [ids], C(1), V(1))], b));
            Assert.Equal(new uint[] { 10, 20 }, Column(b, 1));
            Assert.False(state.Changed);
            Assert.Equal(0, db.ReadCounter(ids));
        }

        [Fact]
        public void LookupWithFallbackCallsTheFunctionOnlyWhereTheKeyIsMissing()
        {
            var (db, f) = OneTable();
            var log = new List<(string, uint[])>();
            var fallback = db.AddExternalFunction(Function((_, args) => args[0] % 2 == 0 ? args[0] * 100 : null, log));
            var state = db.CreateExecutionState();
            var b = Batch((0, [1, 4, 2, 5, 6]));
            var n = state.RunInstrs([new LookupWithFallbackInstr(f, [V(0)], fallback, [V(0)], C(1), V(1))], b);
            Assert.Equal(4, n);
            Assert.Equal(new uint[] { 10, 400, 20, S, 600 }, Column(b, 1));
            Assert.Equal(new uint[] { 4, 5, 6 }, log.Select(x => x.Item2[0]));
        }

        [Fact]
        public void LookupWithFallbackDoesNotCallTheFunctionWhenEverythingIsFound()
        {
            var (db, f) = OneTable();
            var log = new List<(string, uint[])>();
            var fallback = db.AddExternalFunction(Function((_, args) => 0, log));
            var state = db.CreateExecutionState();
            var b = Batch((0, [1, 2]));
            Assert.Equal(2, state.RunInstrs([new LookupWithFallbackInstr(f, [V(0)], fallback, [], C(1), V(1))], b));
            Assert.Empty(log);
        }

        [Fact]
        public void InsertStagesOneRowPerLiveLane()
        {
            var (db, f) = OneTable();
            var state = db.CreateExecutionState();
            var b = Batch((0, [3, 4, 5]), (1, [30, 40, 50]));
            Instr[] program = [new AssertNeInstr(V(0), K(4)), new InsertInstr(f, [V(0), V(1)])];
            Assert.Equal(2, state.RunInstrs(program, b));
            Assert.True(state.Changed);
            Commit(db, state);
            Assert.Equal("[1, 10] [2, 20] [3, 30] [5, 50]", Dump(db, f));
        }

        [Fact]
        public void InsertWithConstantsAndWideRows()
        {
            var db = new Database(ParallelHeuristics.Serial);
            var w = db.AddTable(Plain(4, 6));
            var state = db.CreateExecutionState();
            var b = Batch((0, [1, 2]), (1, [5, 6]));
            Assert.Equal(2, state.RunInstrs([new InsertInstr(w, [K(9), V(0), K(8), V(1), V(0), K(7)])], b));
            Commit(db, state);
            Assert.Equal("[9, 1, 8, 5, 1, 7] [9, 2, 8, 6, 2, 7]", Dump(db, w));
        }

        [Fact]
        public void InsertIfEqStagesWhereTheOperandsAreEqual()
        {
            var db = new Database(ParallelHeuristics.Serial);
            var t = db.AddTable(Plain(1, 1));
            var state = db.CreateExecutionState();
            var b = Batch((0, [1, 2, 3, 4]), (1, [1, 0, 3, 0]));
            Assert.Equal(4, state.RunInstrs([new InsertIfEqInstr(t, V(0), V(1), [V(0)])], b));
            Assert.Equal(4, state.RunInstrs([new InsertIfEqInstr(t, V(1), K(0), [K(100)])], b));
            Assert.Equal(4, state.RunInstrs([new InsertIfEqInstr(t, K(4), V(0), [K(200)])], b));
            Assert.Equal(4, state.RunInstrs([new InsertIfEqInstr(t, K(4), K(4), [K(300)])], b));
            Assert.Equal(4, state.RunInstrs([new InsertIfEqInstr(t, K(4), K(5), [K(400)])], b));
            Commit(db, state);
            Assert.Equal("[1] [3] [100] [200] [300]", Dump(db, t));
        }

        [Fact]
        public void InsertIfEqThatStagesNothingLeavesChangedAlone()
        {
            var db = new Database(ParallelHeuristics.Serial);
            var t = db.AddTable(Plain(1, 1));
            var state = db.CreateExecutionState();
            var b = Batch((0, [1, 2]));
            state.RunInstrs([new InsertIfEqInstr(t, V(0), K(9), [V(0)])], b);
            state.RunInstrs([new InsertIfEqInstr(t, K(1), K(2), [V(0)])], b);
            Assert.False(state.Changed);
            Assert.Empty(db.Catalog.Notifications.Reset());
        }

        [Fact]
        public void RemoveStagesOneRemovalPerLiveLane()
        {
            var (db, f) = OneTable();
            Fill(db, f, [3, 30]);
            var state = db.CreateExecutionState();
            var b = Batch((0, [1, 3, 9]));
            Assert.Equal(3, state.RunInstrs([new RemoveInstr(f, [V(0)])], b));
            Assert.True(state.Changed);
            Commit(db, state);
            Assert.Equal("[2, 20]", Dump(db, f));
        }

        [Fact]
        public void ExternalBindsResultsAndDropsFailures()
        {
            var db = new Database(ParallelHeuristics.Serial);
            var log = new List<(string, uint[])>();
            var add = db.AddExternalFunction(Function((_, a) => (a[0] + a[1]) % 3 == 0 ? null : a[0] + a[1], log));
            var state = db.CreateExecutionState();
            var b = Batch((0, [1, 2, 3, 4]), (1, [1, 1, 1, 1]));
            Assert.Equal(3, state.RunInstrs([new ExternalInstr(add, [V(0), V(1)], V(2))], b));
            Assert.Equal(new uint[] { 2, S, 4, 5 }, Column(b, 2));
            Assert.Equal(4, log.Count);
            Assert.False(state.Changed);
        }

        [Fact]
        public void ExternalFunctionsSeeTheCallingStateAndCanStage()
        {
            var db = new Database(ParallelHeuristics.Serial);
            var t = db.AddTable(Plain(1, 1));
            ExecutionState? seen = null;
            var stage = db.AddExternalFunction(Function((s, a) =>
            {
                seen = s;
                s.StageInsert(t, a);
                return a[0];
            }));
            var state = db.CreateExecutionState();
            var b = Batch((0, [4, 5]));
            Assert.Equal(2, state.RunInstrs([new ExternalInstr(stage, [V(0)], V(1))], b));
            Assert.Same(state, seen);
            Assert.True(state.Changed);
            Commit(db, state);
            Assert.Equal("[4] [5]", Dump(db, t));
        }

        [Fact]
        public void ExternalWithFallbackCallsTheFallbackWhereTheFirstFails()
        {
            var db = new Database(ParallelHeuristics.Serial);
            var log = new List<(string, uint[])>();
            var even = db.AddExternalFunction(Function((_, a) => a[0] % 2 == 0 ? a[0] : null, log, "even"));
            var small = db.AddExternalFunction(Function((_, a) => a[0] < 5 ? a[0] + 100 : null, log, "small"));
            var state = db.CreateExecutionState();
            var b = Batch((0, [1, 2, 3, 7, 8]));
            Assert.Equal(4, state.RunInstrs([new ExternalWithFallbackInstr(even, [V(0)], small, [V(0)], V(1))], b));
            Assert.Equal(new uint[] { 101, 2, 103, S, 8 }, Column(b, 1));
            Assert.Equal(new[] { "even", "even", "even", "even", "even", "small", "small", "small" }, log.Select(x => x.Item1));
            Assert.Equal(new uint[] { 1, 3, 7 }, log.Skip(5).Select(x => x.Item2[0]));
        }

        [Fact]
        public void AssertionsFilterLanes()
        {
            var db = new Database(ParallelHeuristics.Serial);
            var state = db.CreateExecutionState();
            var b = Batch((0, [1, 2, 3, 4]), (1, [1, 0, 3, 0]));
            Assert.Equal(2, state.RunInstrs([new AssertEqInstr(V(0), V(1))], b));
            Assert.Equal(2, state.RunInstrs([new AssertNeInstr(V(0), V(1))], b));
            Assert.Equal(1, state.RunInstrs([new AssertEqInstr(V(0), K(3))], b));
            Assert.Equal(3, state.RunInstrs([new AssertNeInstr(K(3), V(0))], b));
            Assert.Equal(4, state.RunInstrs([new AssertEqInstr(K(3), K(3))], b));
            Assert.Equal(0, state.RunInstrs([new AssertEqInstr(K(3), K(4))], b));
            Assert.Equal(4, state.RunInstrs([new AssertNeInstr(K(3), K(4))], b));
            Assert.Equal(0, state.RunInstrs([new AssertNeInstr(K(3), K(3))], b));
            Assert.Equal(1, state.RunInstrs([new AssertEqInstr(V(0), V(1)), new AssertNeInstr(V(0), K(1))], b));
        }

        [Fact]
        public void AssertAnyNeKeepsLanesWhereTheListsDiffer()
        {
            var db = new Database(ParallelHeuristics.Serial);
            var state = db.CreateExecutionState();
            var b = Batch((0, [1, 2, 3, 4]), (1, [1, 2, 3, 4]), (2, [5, 6, 7, 8]), (3, [5, 0, 7, 0]));
            Assert.Equal(2, state.RunInstrs([AssertAnyNeInstr.Create([V(0), V(2)], [V(1), V(3)])], b));
            Assert.Equal(0, state.RunInstrs([AssertAnyNeInstr.Create([V(0)], [V(1)])], b));
            Assert.Equal(3, state.RunInstrs([AssertAnyNeInstr.Create([V(0), K(7)], [V(1), V(2)])], b));
            Assert.Equal(0, state.RunInstrs([AssertAnyNeInstr.Create([], [])], b));

            // Uneven lists compare over the shorter one, as egglog's zip does.
            Assert.Equal(0, state.RunInstrs([new AssertAnyNeInstr([V(0), V(1), V(2)], 1)], b));
        }

        [Fact]
        public void ReadCounterBroadcastsToEveryLane()
        {
            var db = new Database(ParallelHeuristics.Serial);
            var ts = db.AddCounter();
            db.IncrementCounter(ts);
            db.IncrementCounter(ts);
            var state = db.CreateExecutionState();
            var b = Batch((0, [1, 2, 3]));
            Assert.Equal(2, state.RunInstrs([new AssertNeInstr(V(0), K(2)), new ReadCounterInstr(ts, V(1))], b));

            // Dead lanes get the value too.
            Assert.Equal(new uint[] { 2, 2, 2 }, Column(b, 1));
            Assert.Equal(2, db.ReadCounter(ts));
        }

        [Fact]
        public void AJoinWithoutVariablesRunsOncePerBatch()
        {
            var db = new Database(ParallelHeuristics.Serial);
            var t = db.AddTable(Plain(1, 2));
            var ts = db.AddCounter();
            var state = db.CreateExecutionState();
            var b = new Bindings();
            var empty = new DenseIdMap<Variable, Value>();
            for (var i = 0; i < 7; i++)
                b.Push(empty, ReadOnlySpan<Variable>.Empty);

            Assert.Equal(1, state.RunInstrs([new ReadCounterInstr(ts, V(0)), new InsertInstr(t, [K(1), V(0)])], b));
            Assert.Equal(1, b.Matches);
            Commit(db, state);
            Assert.Equal("[1, 0]", Dump(db, t));

            // An empty batch with no variables runs once as well.
            Assert.Equal(1, db.CreateExecutionState().RunInstrs(Array.Empty<Instr>(), new Bindings()));
        }

        [Fact]
        public void TheProgramStopsWhenNoLaneIsLeft()
        {
            var db = new Database(ParallelHeuristics.Serial);
            var t = db.AddTable(Plain(1, 1));
            var log = new List<(string, uint[])>();
            var f = db.AddExternalFunction(Function((_, a) => 1, log));
            var state = db.CreateExecutionState();
            var b = Batch((0, [1, 2]));
            Instr[] program = [new AssertEqInstr(V(0), K(9)), new ExternalInstr(f, [V(0)], V(1)), new InsertInstr(t, [V(0)])];
            Assert.Equal(0, state.RunInstrs(program, b));
            Assert.Empty(log);
            Assert.False(state.Changed);
            Assert.False(b.Contains(V(1)));

            // A batch with variables but no matches does nothing at all.
            var none = Batch((0, Array.Empty<uint>()));
            Assert.Equal(0, state.RunInstrs([new InsertInstr(t, [V(0)])], none));
            Assert.Equal(0, state.RunInstrs(Array.Empty<Instr>(), none));
        }

        [Fact]
        public void ARedefinedVariableIsReadBeforeItIsWritten()
        {
            // egglog evaluates an instruction's operands before it inserts the destination, so an instruction that
            // redefines one of its operands reads the old values.
            var (db, f) = OneTable();
            var plusOne = db.AddExternalFunction(Function((_, a) => a[0] + 1));
            var state = db.CreateExecutionState();
            var b = Batch((0, [1, 2, 3]));
            Assert.Equal(3, state.RunInstrs([new ExternalInstr(plusOne, [V(0), V(0)], V(0))], b));
            Assert.Equal(new uint[] { 2, 3, 4 }, Column(b, 0));
            Assert.Equal(1, state.RunInstrs([new LookupInstr(f, [V(0)], C(1), V(0))], b));
            Assert.Equal(new uint[] { 20, S, S }, Column(b, 0));
            Assert.Equal(3, state.RunInstrs([new LookupWithDefaultInstr(f, [V(0)], C(1), V(0), V(0))], b));
            Assert.Equal(new uint[] { 20, S, S }, Column(b, 0));
        }

        [Fact]
        public void FullBatchesUseBothMaskWords()
        {
            var (db, f) = OneTable();
            var state = db.CreateExecutionState();
            var keys = Enumerable.Range(0, 128).Select(i => (uint)(i % 4)).ToArray();
            var b = Batch((0, keys));
            Assert.Equal(64, state.RunInstrs([new LookupInstr(f, [V(0)], C(1), V(1))], b));
            Assert.Equal(keys.Select(k => k == 1 ? 10u : k == 2 ? 20u : S), Column(b, 1));
        }

        [Fact]
        public void UnboundVariablesAndUnknownIdsThrow()
        {
            var (db, f) = OneTable();
            var state = db.CreateExecutionState();
            Assert.Throws<InvalidOperationException>(() => state.RunInstrs([new LookupInstr(f, [V(5)], C(1), V(1))], Batch((0, [1]))));
            Assert.Throws<InvalidOperationException>(() => state.RunInstrs([new InsertInstr(f, [V(0), V(5)])], Batch((0, [1]))));
            Assert.Throws<KeyNotFoundException>(() => state.RunInstrs([new ExternalInstr(new ExternalFunctionId(3), [V(0)], V(1))], Batch((0, [1]))));
            Assert.Throws<ArgumentException>(() => state.RunInstrs([new LookupInstr(f, [V(0), V(0)], C(1), V(1))], Batch((0, [1]))));
            Assert.Throws<ArgumentOutOfRangeException>(() => state.RunInstrs([new LookupInstr(f, [V(0)], C(2), V(1))], Batch((0, [1]))));
            Assert.Throws<ArgumentNullException>(() => state.RunInstrs((Instr[])null!, new Bindings()));
            Assert.Throws<ArgumentNullException>(() => state.RunInstrs(Array.Empty<Instr>(), null!));

            // A default row of the wrong width fails before an id is minted.
            var ids = db.AddCounter();
            Assert.Throws<InvalidOperationException>(() => state.RunInstrs([new LookupOrInsertDefaultInstr(f, [V(0)], [ids, ids], C(1), V(1))], Batch((0, [7]))));
            Assert.Equal(0, db.ReadCounter(ids));

            // Counters and external functions need a database.
            var bare = TableTestUtil.EmptyState();
            Assert.Throws<InvalidOperationException>(() => bare.RunInstrs([new ReadCounterInstr(new CounterId(0), V(0))], new Bindings()));
        }

        [Fact]
        public void LookupsReadTablesThroughTheView()
        {
            // A state over a bare catalog reads and writes through the catalog.
            var catalog = new TableCatalog();
            var table = Plain(1, 2);
            var id = catalog.Add(table);
            TableTestUtil.Stage(table, [1, 10]);
            table.Merge(TableTestUtil.EmptyState());
            var state = new ExecutionState(catalog);
            var b = Batch((0, [1, 2]));
            Assert.Equal(1, state.RunInstrs([new LookupInstr(id, [V(0)], C(1), V(1)), new InsertInstr(id, [V(1), V(0)])], b));
            state.Flush();
            table.Merge(state);
            Assert.Equal(2, table.Count);
        }

    }

}

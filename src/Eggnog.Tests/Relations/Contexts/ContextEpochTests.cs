using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

using Xunit;

using static Eggnog.Relations.Tests.QueryTestUtil;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// Tests for the clock and the epoch pin: an execution state reads the store clock as its own timestamp, so every
    /// write of a run carries the run's epoch however far other contexts advance the clock, and a root that runs alone
    /// numbers its rows exactly as egglog does.
    /// </summary>
    [Collection(ExecutorStressCollection.Name)]
    public class ContextEpochTests
    {

        /// <summary>
        /// Gets the execution modes, as theory data.
        /// </summary>
        public static IEnumerable<object[]> ModesData => ExecutionTestUtil.Modes.Select(m => new object[] { m });

        /// <summary>
        /// <c>num(value, id, ts)</c>: keyed by the value, the id merged by union, sorted by the timestamp.
        /// </summary>
        static SortedWritesTable Num(TableId uf, ParallelHeuristics p) =>
            new(1, 3, C(2), [C(1)], new UnionIdMerge(C(1), uf, C(2)), p);

        /// <summary>
        /// <c>add(x, y, id, ts)</c>: keyed by the operands, the id merged by union, sorted by the timestamp.
        /// </summary>
        static SortedWritesTable Add(TableId uf, ParallelHeuristics p) =>
            new(2, 4, C(3), [C(0), C(1), C(2)], new UnionIdMerge(C(2), uf, C(3)), p);

        /// <summary>
        /// A root state, its clone and a child's state each read the clock as their own epoch while a sibling advances
        /// the clock on another thread; other counters read live, and a database without a store pins nothing.
        /// </summary>
        [Fact]
        public void ReadingTheClockReturnsTheStatesEpochWhileASiblingAdvancesIt()
        {
            var store = new Store();
            var root = store.Root;
            var child = root.CreateChild();
            var sibling = root.CreateChild();
            var clock = store.ClockCounter;

            var epoch = root.BeginEpoch();
            var state = root.Database.CreateExecutionState(epoch);
            var clone = state.Clone();
            var childEpoch = child.BeginEpoch();
            var childState = child.Database.CreateExecutionState(childEpoch);

            var stop = 0;
            using var started = new ManualResetEventSlim();
            var advancer = ConcurrencyTestThread.Start(() =>
            {
                started.Set();
                while (Volatile.Read(ref stop) == 0)
                    sibling.EndEpoch();
            });

            started.Wait();
            try
            {
                // Read until the sibling has advanced the clock well past both epochs (bounded in time, for a loaded
                // machine), and at least 10 000 times.
                var observedLive = 0L;
                var watch = System.Diagnostics.Stopwatch.StartNew();
                for (var i = 0; i < 10_000 || (observedLive <= epoch + 1000 && watch.Elapsed < TimeSpan.FromSeconds(30)); i++)
                {
                    Assert.Equal(epoch, state.ReadCounter(clock));
                    Assert.Equal(epoch, clone.ReadCounter(clock));
                    Assert.Equal(childEpoch, childState.ReadCounter(clock));
                    observedLive = root.Database.ReadCounter(clock);
                }

                Assert.True(observedLive > epoch, "the sibling never advanced the clock");
            }
            finally
            {
                Volatile.Write(ref stop, 1);
                advancer.Join();
            }

            // Other counters read live, and a database without a store pins nothing.
            store.Root.NewId();
            Assert.Equal(root.Database.ReadCounter(store.IdCounter), state.ReadCounter(store.IdCounter));
            var bare = new Database();
            var bareClock = bare.AddCounter();
            var bareState = bare.CreateExecutionState(7);
            bare.IncrementCounter(bareClock);
            Assert.Equal(1, bareState.ReadCounter(bareClock));
            Assert.Equal(0u, bare.CreateExecutionState().Timestamp);
        }

        /// <summary>
        /// A state created without a timestamp, at the root or in a child, takes the store clock's value.
        /// </summary>
        [Fact]
        public void NullTimestampsResolveToTheStoreClock()
        {
            var store = new Store();
            var db = store.Root.Database;
            store.AdvanceClock();
            store.AdvanceClock();
            Assert.Equal(3u, db.CreateExecutionState().Timestamp);
            Assert.Equal(3u, db.WithExecutionState(s => s.Timestamp));
            Assert.Equal(5u, db.CreateExecutionState(5).Timestamp);
            Assert.Equal(3u, store.Root.CreateChild().Database.CreateExecutionState().Timestamp);
        }

        /// <summary>
        /// A container rebuild with no timestamp stamps the unions a clock-reading <see cref="UnionContainerMerge"/>
        /// stages with the clock's value, at a store's root (through the pinned state) as on a bare database (through
        /// the live counter), which is egglog's behaviour.
        /// </summary>
        [Fact]
        public void ContainerRebuildsAtAStoreRootStampTheClockAsEgglogDoes()
        {
            uint Stamp(Database db, TableId uf, CounterId ids, CounterId clock)
            {
                db.Containers.RegisterType<VecContainer>(ids, new UnionContainerMerge(uf, clock));
                var a = (uint)db.IncrementCounter(ids);
                var b = (uint)db.IncrementCounter(ids);
                db.WithExecutionState(s => s.Containers.GetOrInsert(new VecContainer(a), s));
                db.WithExecutionState(s => s.Containers.GetOrInsert(new VecContainer(b), s));
                db.WithExecutionState(s => { s.StageInsert(uf, [a, b, 5]); return 0; }, 5);
                db.MergeAll(5);
                db.Counters.IncrementBy(clock, 10);

                Assert.True(db.RebuildContainers(uf).Changed);
                db.MergeAll(11);
                var unions = ((DisplacedTable)db.GetTable(uf)).UnionFind;
                Assert.Equal(2, unions.Count);
                return unions.GetTimestamp(1);
            }

            var bare = new Database(ParallelHeuristics.Serial);
            var bareUf = bare.AddTable(new DisplacedTable(), name: "$uf");
            var bareIds = bare.AddCounter();
            var bareClock = bare.AddCounter();
            bare.IncrementCounter(bareClock);
            var store = new Store(ParallelHeuristics.Serial);

            Assert.Equal(11u, Stamp(bare, bareUf, bareIds, bareClock));
            Assert.Equal(11u, Stamp(store.Root.Database, store.UnionFindTable, store.IdCounter, store.ClockCounter));
        }

        /// <summary>
        /// Every row and union a rule run writes carries the run's epoch, in every execution mode, while an external
        /// function the rule calls advances the clock on every match.
        /// </summary>
        [Theory]
        [MemberData(nameof(ModesData))]
        public void EveryRowOfARunHasTheRunsEpoch(string mode)
        {
            const int n = 300;
            const uint collide = 1_000_000;
            var p = ExecutionTestUtil.Parallelism(mode);
            var store = new Store(p);
            var root = store.Root;
            var db = root.Database;
            ExecutionTestUtil.Configure(db, mode);
            var uf = store.UnionFindTable;
            var num = store.AddTable(Num(uf, p), [], [uf], "num");
            var seen = store.AddTable(new SortedWritesTable(1, 3, C(2), [], new KeepCurrentMerge(), p), name: "seen");

            // A sibling that advances the clock from inside the run: every call of the external moves it on.
            var sibling = root.CreateChild();
            var tick = db.AddExternalFunction(ExternalFunction.Create((ExecutionState state, ReadOnlySpan<uint> args, out uint result) =>
            {
                sibling.EndEpoch();
                result = args[0];
                return true;
            }));

            var load = root.BeginEpoch();
            db.WithExecutionState(s =>
            {
                for (uint v = 0; v < n; v++)
                    s.StageInsert(num, [v, root.NewId(), load]);

                return 0;
            }, load);
            db.MergeAll(load);
            root.EndEpoch();

            // num(v, i, _) => seen(v, tick(v), next_ts); num(collide, i, next_ts), which unions every id at next_ts.
            var rsb = db.NewRuleSet();
            var q = rsb.NewRule();
            var v1 = q.NewVariable("v");
            var i1 = q.NewVariable("i");
            var t1 = q.NewVariable("t");
            q.AddAtom(num, [v1, i1, t1]);
            var rb = q.Build();
            var ticked = rb.CallExternal(tick, [v1]);
            var now = rb.ReadCounter(store.ClockCounter);
            rb.Insert(seen, [v1, ticked, now]);
            rb.Insert(num, [V(collide), i1, now]);
            rb.Build("stamp");
            using var rs = rsb.Build();

            var epoch = root.BeginEpoch();
            root.NoteRuleRun(epoch);
            var report = db.RunRuleSet(rs, ReportLevel.TimeOnly, epoch);
            Assert.True(report.Changed);
            Assert.True(store.ReadClock() >= epoch + n, "the external did not advance the clock");

            var seenRows = DatabaseTestUtil.Rows(db, seen);
            Assert.Equal(n, seenRows.Count);
            Assert.All(seenRows, r => Assert.Equal(epoch, r[2]));
            var collided = DatabaseTestUtil.Rows(db, num).Single(r => r[0] == collide);
            Assert.Equal(epoch, collided[2]);

            // Every union the merge staged carries the epoch too.
            var unions = store.RootUnionFind.UnionFind;
            Assert.Equal(n - 1, unions.Count);
            Assert.All(unions.Timestamps.ToArray(), ts => Assert.Equal(epoch, ts));
        }

        /// <summary>
        /// A store's root that runs alone stamps its rows and unions exactly as a bare database laid out the same way,
        /// which reads every counter live as egglog does: pinning the clock to each state's epoch changes no timestamp
        /// at a root.
        /// </summary>
        [Fact]
        public void RootTimestampsAreIdenticalToThePreChangeSequence()
        {
            // The same program on a bare database laid out as a store (no clock designated, so every counter reads
            // live, as in egglog) and on a store's root (the clock pinned to each state's timestamp).
            var bare = new Database(ParallelHeuristics.Serial);
            var bareUf = bare.AddTable(new DisplacedTable(), name: "$uf");
            var bareIds = bare.AddCounter();
            var bareClock = bare.AddCounter();
            bare.IncrementCounter(bareClock);
            var bareNum = bare.AddTable(Num(bareUf, ParallelHeuristics.Serial), [], [bareUf], "num");
            var bareAdd = bare.AddTable(Add(bareUf, ParallelHeuristics.Serial), [], [bareUf], "add");

            var store = new Store(ParallelHeuristics.Serial);
            var db = store.Root.Database;
            var num = store.AddTable(Num(store.UnionFindTable, ParallelHeuristics.Serial), [], [store.UnionFindTable], "num");
            var add = store.AddTable(Add(store.UnionFindTable, ParallelHeuristics.Serial), [], [store.UnionFindTable], "add");

            Assert.Equal((bareUf, bareIds, bareClock, bareNum, bareAdd), (store.UnionFindTable, store.IdCounter, store.ClockCounter, num, add));

            // Each run starts from empty table pools on this thread: a pooled hash table keeps the slots it last grew
            // to, and its iteration order with them, so tables left in the pool by whatever ran on this thread before
            // would change the order in which the rules match, and so the ids they mint (not what the programs
            // compute).
            DrainTablePools();
            var bareLog = RunAcProgram(bare, bareUf, bareIds, bareClock, bareNum, bareAdd, storeRoot: false);
            DrainTablePools();
            var storeLog = RunAcProgram(db, store.UnionFindTable, store.IdCounter, store.ClockCounter, num, add, storeRoot: true);

            Assert.Equal(bareLog.Count, storeLog.Count);
            for (var i = 0; i < bareLog.Count; i++)
                Assert.Equal(bareLog[i], storeLog[i]);

            Assert.Equal(Snapshot(bare, bareUf, bareIds, bareClock, bareNum, bareAdd), Snapshot(db, store.UnionFindTable, store.IdCounter, store.ClockCounter, num, add));
            Assert.True(((DisplacedTable)db.GetTable(store.UnionFindTable)).UnionFind.Count > 0, "the program made no unions");
        }

        /// <summary>
        /// Runs a small AC program the way egglog's bridge drives a database: load at the clock's value, then for each
        /// iteration run the rules at <c>ts = clock</c>, and rebuild with the clock (passes at ts, ts+1, ...) when the
        /// union-find grew, else advance it (egglog-bridge/src/lib.rs:592-624, 667-676). Actions read the clock through
        /// <see cref="ReadCounterInstr"/>. On a store's root, the null-timestamp entry points are used where the bridge
        /// would read the clock.
        /// </summary>
        /// <returns>A log of what each step returned and the clock after it.</returns>
        static List<string> RunAcProgram(Database db, TableId uf, CounterId ids, CounterId clock, TableId num, TableId add, bool storeRoot)
        {
            const int n = 5;
            var log = new List<string>();
            var ts = (uint)db.ReadCounter(clock);
            var leaves = new uint[n];
            db.WithExecutionState(s =>
            {
                for (var i = 0; i < n; i++)
                {
                    leaves[i] = (uint)s.IncrementCounter(ids);
                    s.StageInsert(num, [(uint)(100 + i), leaves[i], (uint)s.ReadCounter(clock)]);
                }

                var prev = leaves[0];
                for (var i = 1; i < n; i++)
                {
                    var id = (uint)s.IncrementCounter(ids);
                    s.StageInsert(add, [prev, leaves[i], id, (uint)s.ReadCounter(clock)]);
                    prev = id;
                }

                return 0;
            }, storeRoot ? null : ts);
            log.Add("load " + (storeRoot ? db.MergeAll() : db.MergeAll(ts)) + " " + db.ReadCounter(clock));
            db.IncrementCounter(clock);

            for (var iteration = 0; iteration < 4; iteration++)
            {
                ts = (uint)db.ReadCounter(clock);
                var rsb = db.NewRuleSet();

                // add(x, y, z, _) => add(y, x, z, next_ts)
                var comm = rsb.NewRule();
                var cx = comm.NewVariable("x");
                var cy = comm.NewVariable("y");
                var cz = comm.NewVariable("z");
                var ct = comm.NewVariable("t");
                comm.AddAtom(add, [cx, cy, cz, ct]);
                var commRules = comm.Build();
                var cnow = commRules.ReadCounter(clock);
                commRules.Insert(add, [cy, cx, cz, cnow]);
                commRules.Build("comm");

                // add(x, i1, i2, _), add(y, z, i1, _) => add(x, y, r, next_ts), add(r, z, i2, next_ts), r fresh
                var assoc = rsb.NewRule();
                var x = assoc.NewVariable("x");
                var y = assoc.NewVariable("y");
                var z = assoc.NewVariable("z");
                var i1 = assoc.NewVariable("i1");
                var i2 = assoc.NewVariable("i2");
                var t1 = assoc.NewVariable("t1");
                var t2 = assoc.NewVariable("t2");
                assoc.AddAtom(add, [x, i1, i2, t1]);
                assoc.AddAtom(add, [y, z, i1, t2]);
                var assocRules = assoc.Build();
                var now = assocRules.ReadCounter(clock);
                var r = assocRules.LookupOrInsert(add, [x, y], [WriteVal.FromCounter(ids), now], C(2));
                assocRules.Insert(add, [r, z, i2, now]);
                assocRules.Build("assoc");
                using var rs = rsb.Build();

                var before = ((DisplacedTable)db.GetTable(uf)).UnionFind.Count;
                var report = storeRoot ? db.RunRuleSet(rs) : db.RunRuleSet(rs, ReportLevel.TimeOnly, ts);
                log.Add("run " + iteration + " " + report.Changed + " " + db.ReadCounter(clock));
                if (((DisplacedTable)db.GetTable(uf)).UnionFind.Count != before)
                    log.Add("rebuild " + db.Rebuild(uf, [num, add], clock) + " " + db.ReadCounter(clock));
                else
                    db.IncrementCounter(clock);
            }

            return log;
        }

        /// <summary>
        /// Empties the calling thread's pools of index and staging hash tables, so the next program builds every such
        /// table from scratch.
        /// </summary>
        static void DrainTablePools()
        {
            Drain(SharedPool<RawHashTable<ColumnIndexEntry>, ColumnIndexHashPolicy>.Instance);
            Drain(SharedPool<RawHashTable<TupleIndexEntry>, TupleIndexHashPolicy>.Instance);
            Drain(SharedPool<RawHashTable<TableEntry>, StagedOutputsHashPolicy>.Instance);
        }

        /// <summary>
        /// Takes, and drops, every object the calling thread's state of <paramref name="pool"/> holds.
        /// </summary>
        static void Drain<T, TPolicy>(ObjectPool<T, TPolicy> pool)
            where T : class
            where TPolicy : struct, IPoolPolicy<T>
        {
            while (pool.CountOnCurrentThread > 0)
                pool.Rent();
        }

        /// <summary>
        /// Captures every row of both tables in physical order, the union-find's rows with their timestamps, and both
        /// counters.
        /// </summary>
        static string Snapshot(Database db, TableId uf, CounterId ids, CounterId clock, TableId num, TableId add)
        {
            var displaced = ((DisplacedTable)db.GetTable(uf)).UnionFind;
            var unions = Enumerable.Range(0, displaced.Count).Select(row => displaced.GetChild(row) + "->" + displaced.GetCanonical(row) + "@" + displaced.GetTimestamp(row));
            return string.Join(" ", TableTestUtil.LiveRows(db.GetTable(num)).Select(TableTestUtil.Show)) + " | "
                + string.Join(" ", TableTestUtil.LiveRows(db.GetTable(add)).Select(TableTestUtil.Show)) + " | "
                + string.Join(" ", unions) + " | " + db.ReadCounter(ids) + " " + db.ReadCounter(clock);
        }

    }

}

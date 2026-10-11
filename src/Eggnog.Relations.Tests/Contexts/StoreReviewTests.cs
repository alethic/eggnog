using System;
using System.Threading;

using Xunit;

using static Eggnog.Relations.Tests.QueryTestUtil;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// Review tests for the store's clock limit and its schema (colored-contexts spec §3.2, §4.1-4.3): an epoch the
    /// clock hands out must be one the run's actions can read, and every table of a store's root must be known to the
    /// store's schema, which children read instead of the root's table map.
    /// </summary>
    public class StoreReviewTests
    {

        /// <summary>
        /// §3.2: <c>ReadClock</c> is "checked: OverflowException at uint.MaxValue". The clock's value
        /// <see cref="uint.MaxValue"/> is <see cref="Value.StaleRep"/>, which <c>ValueFromCounter</c> (the §4.2 defect
        /// fix) rejects, so an epoch of <see cref="uint.MaxValue"/> could not be read by <c>next_ts</c>.
        /// </summary>
        [Fact]
        public void ReadClockOverflowsAtTheStaleMarker()
        {
            var store = new Store();
            store.Counters.IncrementBy(store.ClockCounter, uint.MaxValue - 1L);
            Assert.Equal(uint.MaxValue, store.Counters.Read(store.ClockCounter));
            Assert.Throws<OverflowException>(() => store.ReadClock());
            Assert.Throws<OverflowException>(() => store.Root.BeginEpoch());
        }

        /// <summary>
        /// Whatever epoch <see cref="Context.BeginEpoch"/> hands out must be usable by the run it brackets: a rule that
        /// reads the clock (<see cref="ReadCounterInstr"/>, egglog's <c>next_ts</c>) stamps its rows with it. Either the
        /// clock refuses the epoch up front, or the run succeeds; a run that starts and then throws from inside its
        /// actions is the defect.
        /// </summary>
        [Fact]
        public void AnEpochTheClockHandsOutIsReadableByTheRunsActions()
        {
            var store = new Store(ParallelHeuristics.Serial);
            var db = store.Root.Database;
            var f = store.AddTable(new SortedWritesTable(1, 2, C(1), [], new KeepCurrentMerge(), ParallelHeuristics.Serial), name: "f");
            var g = store.AddTable(new SortedWritesTable(1, 2, C(1), [], new KeepCurrentMerge(), ParallelHeuristics.Serial), name: "g");
            db.WithExecutionState(s =>
            {
                s.StageInsert(f, [7, 1]);
                return 0;
            }, 1);
            db.MergeAll(1);

            // f(x, _) => g(x, next_ts)
            var rsb = db.NewRuleSet();
            var q = rsb.NewRule();
            var x = q.NewVariable("x");
            var t = q.NewVariable("t");
            q.AddAtom(f, [x, t]);
            var rb = q.Build();
            var now = rb.ReadCounter(store.ClockCounter);
            rb.Insert(g, [x, now]);
            rb.Build("stamp");
            using var rs = rsb.Build();

            store.Counters.IncrementBy(store.ClockCounter, uint.MaxValue - store.Counters.Read(store.ClockCounter));
            uint epoch;
            try
            {
                epoch = store.Root.BeginEpoch();
            }
            catch (OverflowException)
            {
                return;
            }

            db.RunRuleSet(rs, ReportLevel.TimeOnly, epoch);
            Assert.Equal(epoch, DatabaseTestUtil.Rows(db, g)[0][1]);
        }

        /// <summary>
        /// §4.3: children reach root tables only through the store's <see cref="LayeredSchema"/>, and the root's rebuild
        /// pass rebuilds <see cref="LayeredSchema.FunctionTables"/> (§7.1). A table added through the root database's own
        /// <see cref="Database.AddTable"/> must therefore either be refused (definitions go through the store, as
        /// <see cref="Database.AddCounter"/> is refused once children exist, X16) or be registered with the schema.
        /// </summary>
        [Fact]
        public void ATableAddedThroughAStoreRootsDatabaseIsKnownToTheStore()
        {
            var store = new Store(ParallelHeuristics.Serial);
            var child = store.Root.CreateChild();
            TableId direct;
            try
            {
                direct = store.Root.Database.AddTable(StoreTests.Function(store), [], [store.UnionFindTable], "direct");
            }
            catch (InvalidOperationException)
            {
                return;
            }

            Assert.NotNull(store.Schema[direct]);
            Assert.Contains(direct, store.Schema.FunctionTables.ToArray());
            Assert.Equal(store.Root.Database.TableCount, child.Database.TableCount);
            Assert.Equal("direct", child.Database.GetTableName(direct));
        }

        /// <summary>
        /// §4.3, X16: tables may be added while other threads read the schema (children of a frozen root, M9.2 on),
        /// and every registered id the schema lists must have its <see cref="LayeredTableSchema"/>: a reader that
        /// finds an id in <see cref="LayeredSchema.FunctionTables"/> must find <c>schema[id]</c>.
        /// </summary>
        [Fact]
        public void EveryListedFunctionTableHasItsSchemaWhileTablesAreAdded()
        {
            var store = new Store(ParallelHeuristics.Serial);
            var schema = store.Schema;
            var stop = 0;
            var missing = 0;
            var reader = ConcurrencyTestThread.Start(() =>
            {
                while (Volatile.Read(ref stop) == 0)
                {
                    var listed = schema.FunctionTables;
                    if (listed.Length > 0 && schema[listed[^1]] is null)
                        Interlocked.Increment(ref missing);
                }
            });

            try
            {
                for (var i = 0; i < 20_000 && Volatile.Read(ref missing) == 0; i++)
                    store.AddTable(StoreTests.Function(store));
            }
            finally
            {
                Volatile.Write(ref stop, 1);
                reader.Join();
            }

            Assert.Equal(0, missing);
        }

    }

}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

using Xunit;

using static Eggnog.Relations.Tests.QueryTestUtil;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// The store's clock limit: the clock is checked and throws at <see cref="uint.MaxValue"/>, the stale-row marker
    /// <see cref="Value.StaleRep"/>, so every epoch it hands out can be used: a rule run at that epoch reads the clock,
    /// and a row stamped with it is visible, even in a zero-key table whose sort column is its first cell.
    /// </summary>
    public class StoreClockTests
    {

        /// <summary>
        /// The clock is checked and throws at <see cref="uint.MaxValue"/>, the stale-row marker, because an epoch equal
        /// to <see cref="Value.StaleRep"/> cannot be used: a rule reading the clock at that epoch throws
        /// (<c>ValueFromCounter</c> rejects it). Either <see cref="Store.ReadClock"/> refuses the epoch, or a rule run
        /// at the epoch it returned can read it.
        /// </summary>
        [Fact]
        public void AnEpochTheStoreHandsOutCanBeReadByARuleAtThatEpoch()
        {
            var store = new Store(ParallelHeuristics.Serial);
            var db = store.Root.Database;
            var seen = store.AddTable(new SortedWritesTable(1, 2, C(1), [], new KeepCurrentMerge(), ParallelHeuristics.Serial), name: "seen");
            var src = store.AddTable(new SortedWritesTable(1, 2, C(1), [], new KeepCurrentMerge(), ParallelHeuristics.Serial), name: "src");
            store.Counters.IncrementBy(store.ClockCounter, uint.MaxValue - 1L);

            uint epoch;
            try
            {
                epoch = store.Root.BeginEpoch();
            }
            catch (OverflowException)
            {
                return; // the clock is exhausted at uint.MaxValue
            }

            db.WithExecutionState(s => { s.StageInsert(src, [1, epoch]); return 0; }, epoch);
            db.MergeAll(epoch);

            var rsb = db.NewRuleSet();
            var q = rsb.NewRule();
            var x = q.NewVariable("x");
            var t = q.NewVariable("t");
            q.AddAtom(src, [x, t]);
            var rb = q.Build();
            var now = rb.ReadCounter(store.ClockCounter);
            rb.Insert(seen, [x, now]);
            rb.Build("r");
            using var rs = rsb.Build();

            // The store handed out uint.MaxValue as a valid epoch; a rule run at it must not throw.
            var report = db.RunRuleSet(rs, ReportLevel.TimeOnly, epoch);
            Assert.True(report.Changed);
        }

        /// <summary>
        /// The same epoch stamped into a zero-key table whose sort column is its first cell (the store's own nullary
        /// contradiction shape, <see cref="StoreTests"/>) writes <see cref="Value.StaleRep"/> into cell 0, the
        /// deleted-row marker: the row must still be visible.
        /// </summary>
        [Fact]
        public void ARowStampedWithAnEpochTheStoreHandsOutIsVisible()
        {
            var store = new Store(ParallelHeuristics.Serial);
            var db = store.Root.Database;
            var bottom = store.AddTable(new SortedWritesTable(0, 1, C(0), [], new KeepCurrentMerge(), ParallelHeuristics.Serial), name: "bottom");
            store.Counters.IncrementBy(store.ClockCounter, uint.MaxValue - 1L);

            uint epoch;
            try
            {
                epoch = store.Root.BeginEpoch();
            }
            catch (OverflowException)
            {
                return; // the clock is exhausted at uint.MaxValue
            }

            db.WithExecutionState(s => { s.StageInsert(bottom, [s.Timestamp]); return 0; }, epoch);
            db.MergeAll(epoch);
            Assert.Single(TableTestUtil.LiveRows(db.GetTable(bottom)));
        }

        /// <summary>
        /// <c>ReadClock</c> is checked: <see cref="OverflowException"/> at <see cref="uint.MaxValue"/>. The clock's
        /// value <see cref="uint.MaxValue"/> is <see cref="Value.StaleRep"/>, which <c>ValueFromCounter</c> rejects, so
        /// an epoch of <see cref="uint.MaxValue"/> could not be read by <c>next_ts</c>.
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
        /// Whatever epoch <see cref="Context.BeginEpoch"/> hands out is usable by the run it brackets: a rule that
        /// reads the clock (<see cref="ReadCounterInstr"/>, egglog's <c>next_ts</c>) stamps its rows with it. Either
        /// the clock refuses the epoch up front, or the run succeeds; it never starts and then throws from inside its
        /// actions.
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

    }

}

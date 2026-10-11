using System;
using System.Collections.Generic;
using System.Linq;

using Xunit;

using static Eggnog.Relations.Tests.QueryTestUtil;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// Adversarial soundness probes for M9.1 (colored-contexts spec §3, §4, §8.3). Each test states the property the
    /// spec requires; a failure exposes a defect.
    /// </summary>
    public class ContextSoundnessReviewTests
    {

        /// <summary>
        /// §3.2 / §13 edge 25: the clock is checked and throws at <see cref="uint.MaxValue"/>, the stale-row marker,
        /// because an epoch equal to <see cref="Value.StaleRep"/> cannot be used: a rule reading the clock at that epoch
        /// throws (<c>ValueFromCounter</c> rejects it, §4.2 defect fix). Either <see cref="Store.ReadClock"/> refuses the
        /// epoch, or a rule run at the epoch it returned must be able to read it.
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
                return; // the spec's behaviour: the clock is exhausted at uint.MaxValue
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
                return; // the spec's behaviour: the clock is exhausted at uint.MaxValue
            }

            db.WithExecutionState(s => { s.StageInsert(bottom, [s.Timestamp]); return 0; }, epoch);
            db.MergeAll(epoch);
            Assert.Single(TableTestUtil.LiveRows(db.GetTable(bottom)));
        }

        /// <summary>
        /// §1 (context = copy), §3.4 / X39 / X23: a child that asserts nothing sees what its parent sees. A rule over the
        /// union-find run in a child must either see the root's unions or be refused (X23: a <c>$uf</c> atom in a child
        /// is unsupported). It must not silently read the child's never-written placeholder.
        /// </summary>
        [Fact]
        public void AChildRuleOverTheUnionFindDoesNotSilentlyReadThePlaceholder()
        {
            var store = new Store(ParallelHeuristics.Serial);
            var root = store.Root;
            var uf = store.UnionFindTable;
            var marker = store.AddTable(new SortedWritesTable(1, 2, C(1), [], new KeepCurrentMerge(), ParallelHeuristics.Serial), name: "marker");

            var a = root.NewId();
            var b = root.NewId();
            var e = root.BeginEpoch();
            root.Database.WithExecutionState(s => { s.StageInsert(uf, [b, a, e]); return 0; }, e);
            root.Database.MergeAll(e);
            root.EndEpoch();
            Assert.Equal(1, store.RootUnionFind.UnionFind.Count);

            var child = root.CreateChild();

            int RunOver(Database db)
            {
                var rsb = db.NewRuleSet();
                var q = rsb.NewRule();
                var x = q.NewVariable("x");
                var y = q.NewVariable("y");
                var t = q.NewVariable("t");
                q.AddAtom(uf, [x, y, t]);
                var rb = q.Build();
                rb.Build("over-uf");
                using var rs = rsb.Build();
                return (int)db.RunRuleSet(rs, ReportLevel.WithPlan).NumMatches("over-uf");
            }

            Assert.Equal(1, RunOver(root.Database));

            int childMatches;
            try
            {
                childMatches = RunOver(child.Database);
            }
            catch (Exception ex) when (ex is NotSupportedException or InvalidOperationException or KeyNotFoundException)
            {
                return; // refused: acceptable
            }

            Assert.Equal(1, childMatches);
            _ = marker;
        }

        /// <summary>
        /// §1, §3.4, §4.3 (X16, X39): children reach root tables only through the store's schema, and a child that
        /// asserts nothing sees what its parent sees. A table that the store's root database holds must be known to the
        /// schema; adding one to the root database directly after children exist must either be refused or registered.
        /// </summary>
        [Fact]
        public void EveryRootTableIsKnownToTheChildren()
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
                return; // refused: definitions go through the store
            }

            Assert.Equal(store.Root.Database.TableCount, child.Database.TableCount);
            Assert.Equal(store.Root.Database.GetTableSpec(direct).Arity, child.Database.GetTableSpec(direct).Arity);
            Assert.Contains(direct, store.Schema.FunctionTables.ToArray());
        }

        /// <summary>
        /// §4.3 (X16), §8.3 publication: the schema is read lock-free while a table is added, so a reader must never
        /// see a torn schema. Every id listed in <see cref="LayeredSchema.FunctionTables"/> must have a
        /// <see cref="LayeredTableSchema"/> when read right after.
        /// </summary>
        [Fact]
        public void EveryListedFunctionTableHasASchemaWhileTablesAreAdded()
        {
            var store = new Store(ParallelHeuristics.Serial);
            var schema = store.Schema;
            var stop = 0;
            var torn = 0;
            using var start = new System.Threading.ManualResetEventSlim();
            var readers = ConcurrencyTestThread.StartMany(3, _ =>
            {
                start.Wait();
                while (System.Threading.Volatile.Read(ref stop) == 0)
                {
                    var tables = schema.FunctionTables;
                    if (tables.Length > 0 && schema[tables[^1]] is null)
                        System.Threading.Interlocked.Increment(ref torn);
                }
            });

            start.Set();
            try
            {
                for (var i = 0; i < 20_000 && System.Threading.Volatile.Read(ref torn) == 0; i++)
                    store.AddTable(new SortedWritesTable(1, 2, null, [], new KeepCurrentMerge(), ParallelHeuristics.Serial));
            }
            finally
            {
                System.Threading.Volatile.Write(ref stop, 1);
                ConcurrencyTestThread.JoinAll(readers);
            }

            Assert.Equal(0, torn);
        }

    }

}

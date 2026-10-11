using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

using Xunit;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// Tests for <see cref="Store"/> (colored-contexts spec §3.2, §4.1, §4.3): the bridge's layout (union-find table 0,
    /// id counter 0, clock counter 1 starting at 1), definitions at any time, sealed counters, and the clock's limit.
    /// </summary>
    public class StoreTests
    {

        /// <summary>
        /// A function table <c>f(id) -&gt; id, ts</c> merged by union.
        /// </summary>
        internal static SortedWritesTable Function(Store store, int keys = 1) =>
            new(keys, keys + 2, new ColumnId(keys + 1), Enumerable.Range(0, keys + 1).Select(c => new ColumnId(c)).ToArray(), new UnionIdMerge(new ColumnId(keys), store.UnionFindTable, new ColumnId(keys + 1)), ParallelHeuristics.Serial);

        [Fact]
        public void TheUnionFindIsTableZeroAndTheCountersAreZeroAndOne()
        {
            var store = new Store();
            var db = store.Root.Database;

            Assert.Equal(new TableId(0), store.UnionFindTable);
            Assert.Same(store.RootUnionFind, db.GetTable(store.UnionFindTable));
            Assert.Equal("$uf", db.GetTableInfo(store.UnionFindTable).Name);
            Assert.Equal(new CounterId(0), store.IdCounter);
            Assert.Equal(new CounterId(1), store.ClockCounter);
            Assert.Equal(2, store.Counters.Count);
            Assert.Same(db.Counters, store.Counters);
            Assert.Equal(0, db.ReadCounter(store.IdCounter));

            // The clock starts at 1 (egglog-bridge/src/lib.rs:150-152).
            Assert.Equal(1, db.ReadCounter(store.ClockCounter));
            Assert.Equal(1u, store.ReadClock());
            store.AdvanceClock();
            Assert.Equal(2u, store.ReadClock());

            Assert.Same(store, store.Root.Store);
            Assert.True(store.Root.IsRoot);
            Assert.Same(store.Root, db.Owner);
            Assert.Equal(store.ClockCounter.Rep, db.ClockCounterRep);
            Assert.Equal(-1, new Database().ClockCounterRep);
            Assert.Equal(new[] { store.UnionFindTable }, store.Schema.Ids);
            Assert.True(store.Schema.FunctionTables.IsEmpty);
        }

        [Fact]
        public void AddTableRegistersTheTableWithTheSchema()
        {
            var store = new Store();
            var f = store.AddTable(Function(store), [], [store.UnionFindTable], "f");
            var g = store.AddTable(Function(store, 2), [f], [store.UnionFindTable], "g", contextPrivate: true);

            Assert.Equal(new TableId(1), f);
            Assert.Equal(new TableId(2), g);
            Assert.Equal(new[] { f, g }, store.Schema.FunctionTables.ToArray());
            Assert.Equal(new[] { store.UnionFindTable, f, g }, store.Schema.Ids);
            Assert.Equal(3, store.Schema.TableCount);

            var fs = store.Schema[f]!;
            Assert.Equal(f, fs.Id);
            Assert.Equal("f", fs.Name);
            Assert.Same(store.Root.Database.GetTable(f), fs.Root);
            Assert.Same(store.Root.Database.GetTableInfo(f), fs.RootInfo);
            Assert.False(fs.IsContextPrivate);
            Assert.Equal(0, fs.Stratum);
            Assert.Equal(1, fs.KeyCount);
            Assert.Equal(3, fs.Arity);
            Assert.Equal(2, fs.SortColumn);
            Assert.Equal(new[] { new ColumnId(0), new ColumnId(1) }, fs.RebuildColumns.ToArray());
            Assert.True(fs.IsLattice);
            Assert.True(fs.IsConstructor);

            var gs = store.Schema[g]!;
            Assert.True(gs.IsContextPrivate);
            Assert.Equal(1, gs.Stratum);
            Assert.Null(store.Schema[store.UnionFindTable]);
            Assert.Null(store.Schema[new TableId(9)]);

            Assert.Same(DisplacedTable.DisplacedSpec, store.Schema.GetSpec(store.UnionFindTable));
            Assert.Equal(4, store.Schema.GetSpec(g).Arity);
            Assert.Equal("$uf", store.Schema.GetName(store.UnionFindTable));
            Assert.Equal("g", store.Schema.GetName(g));
            Assert.Throws<KeyNotFoundException>(() => store.Schema.GetSpec(new TableId(9)));
            Assert.Throws<KeyNotFoundException>(() => store.Schema.GetName(new TableId(9)));
        }

        [Fact]
        public void AddTableRejectsTablesThatCannotBeLayered()
        {
            var store = new Store();
            Assert.Throws<ArgumentException>(() => store.AddTable(new DisplacedTable()));
            Assert.Equal(1, store.Root.Database.TableCount);

            // A registered table cannot be registered again, and the union-find is never registered.
            var f = store.AddTable(Function(store));
            Assert.Throws<ArgumentException>(() => store.Schema.Register(f, false));
            Assert.Throws<ArgumentException>(() => store.Schema.Register(store.UnionFindTable, false));
        }

        /// <summary>
        /// §4.3, X16, X39: every table of a store is added through <see cref="Store.AddTable"/>, so its schema, which
        /// children read and the root's rebuild walks, knows every root table. The root database's own
        /// <see cref="Database.AddTable"/> is refused, children or not; a bare database and a clone, which belong to no
        /// store, add tables as egglog does.
        /// </summary>
        [Fact]
        public void TheRootDatabaseLeavesDefinitionsToTheStore()
        {
            var store = new Store();
            var db = store.Root.Database;
            void Refused(Action action) => Assert.Equal("Definitions are added through the Store.", Assert.Throws<InvalidOperationException>(action).Message);

            Refused(() => db.AddTable(Function(store)));
            Assert.Equal(1, db.TableCount);
            Assert.Equal(new TableId(1), db.NextTableId);

            var f = store.AddTable(Function(store), name: "f");
            var child = store.Root.CreateChild();
            Refused(() => db.AddTable(Function(store)));
            Assert.Equal(2, db.TableCount);
            Assert.Equal(db.TableCount, child.Database.TableCount);
            Assert.Equal(new[] { f }, store.Schema.FunctionTables.ToArray());

            // Tables added through the store are known to it and to every child at once.
            var g = store.AddTable(Function(store), name: "g");
            Assert.Equal(new[] { f, g }, store.Schema.FunctionTables.ToArray());
            Assert.Equal(db.TableCount, child.Database.TableCount);

            child.Dispose();
            var clone = db.Clone();
            Assert.Null(clone.Owner);
            Assert.Equal(new TableId(3), clone.AddTable(Function(store)));
            Assert.Equal(new TableId(0), new Database().AddTable(new DisplacedTable()));
        }

        [Fact]
        public void AddTableAfterChildrenWorks()
        {
            var store = new Store();
            var f = store.AddTable(Function(store), name: "f");
            var child = store.Root.CreateChild();
            var grandchild = child.CreateChild();

            var g = store.AddTable(Function(store), name: "g");
            Assert.Equal(new TableId(2), g);
            Assert.Equal(3, child.Database.TableCount);
            Assert.Equal(3, grandchild.Database.TableCount);
            Assert.Equal("g", grandchild.Database.GetTableName(g));
            Assert.Equal(3, grandchild.Database.GetTableSpec(g).Arity);
            Assert.Equal(new[] { store.UnionFindTable, f, g }, grandchild.Database.ReadOnlyView.TableIds);
            Assert.Equal(0, store.Root.Database.Dependencies.LevelOf(g));

            // A table may also be added after children were disposed.
            grandchild.Dispose();
            child.Dispose();
            var h = store.AddTable(Function(store), [g], [], "h");
            Assert.Equal(1, store.Schema[h]!.Stratum);
        }

        [Fact]
        public void AddTableFromAnotherThreadThanTheRootsDomainThrows()
        {
            var store = new Store();
            using var held = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            var owner = ConcurrencyTestThread.Start(() =>
            {
                using (store.Root.Domain.Enter())
                {
                    held.Set();
                    release.Wait();
                }
            });

            held.Wait();
            try
            {
                Assert.Throws<InvalidOperationException>(() => store.AddTable(Function(store)));
                Assert.Equal(1, store.Root.Database.TableCount);
            }
            finally
            {
                release.Set();
                owner.Join();
            }

            Assert.Equal(new TableId(1), store.AddTable(Function(store)));
        }

        [Fact]
        public void AddCounterThrowsOnceAChildHasBeenCreated()
        {
            var store = new Store();
            var extra = store.AddCounter();
            Assert.Equal(new CounterId(2), extra);
            Assert.Equal(new CounterId(3), store.Root.Database.AddCounter());

            var child = store.Root.CreateChild();
            Assert.Throws<InvalidOperationException>(() => store.AddCounter());
            Assert.Throws<InvalidOperationException>(() => store.Root.Database.AddCounter());
            Assert.Throws<InvalidOperationException>(() => child.Database.AddCounter());

            // Sealed for good: disposing every child does not reopen it.
            child.Dispose();
            Assert.Throws<InvalidOperationException>(() => store.AddCounter());
            Assert.Equal(4, store.Counters.Count);
        }

        /// <summary>
        /// §3.2, §13 edge 25: the clock is checked and throws at <see cref="uint.MaxValue"/>, which is
        /// <see cref="Value.StaleRep"/>; the last epoch it hands out is one below. Every clock read of the store applies
        /// the bound: <see cref="Store.ReadClock"/>, <see cref="Context.BeginEpoch"/>, a child's creation stamp and the
        /// databases' timestamp defaults.
        /// </summary>
        [Fact]
        public void ReadClockOverflowsAtTheStaleMarker()
        {
            var store = new Store();
            store.Counters.IncrementBy(store.ClockCounter, uint.MaxValue - 2L);
            Assert.Equal(uint.MaxValue - 1, store.ReadClock());
            Assert.Equal(uint.MaxValue - 1, store.Root.BeginEpoch());
            Assert.Equal(uint.MaxValue - 1, store.Root.Database.CreateExecutionState().Timestamp);
            var child = store.Root.CreateChild();
            Assert.Equal(uint.MaxValue - 1, child.CreatedAt);
            Assert.Equal(uint.MaxValue - 1, child.Database.CreateExecutionState().Timestamp);

            store.Root.EndEpoch();
            Assert.Equal(uint.MaxValue, store.Counters.Read(store.ClockCounter));
            Assert.Throws<OverflowException>(() => store.ReadClock());
            Assert.Throws<OverflowException>(() => store.Root.BeginEpoch());
            Assert.Throws<OverflowException>(() => child.BeginEpoch());
            Assert.Throws<OverflowException>(() => store.Root.CreateChild());
            Assert.Throws<OverflowException>(() => store.Root.Database.CreateExecutionState());
            Assert.Throws<OverflowException>(() => child.Database.CreateExecutionState());
            Assert.Throws<OverflowException>(() => store.Root.Database.MergeAll());

            // An explicit timestamp is the caller's; the store's bound applies to clock reads only.
            Assert.Equal(7u, store.Root.Database.CreateExecutionState(7).Timestamp);
        }

        [Fact]
        public void TheContradictionTableIsANullaryRelationDesignatedOnce()
        {
            var store = new Store();
            var f = store.AddTable(Function(store), name: "f");
            var bottom = store.AddTable(new SortedWritesTable(0, 1, new ColumnId(0), [], new KeepCurrentMerge(), ParallelHeuristics.Serial), name: "bottom");
            Assert.Null(store.Schema.Contradiction);

            Assert.Throws<ArgumentException>(() => store.DesignateContradiction(f));
            Assert.Throws<KeyNotFoundException>(() => store.DesignateContradiction(new TableId(7)));
            store.DesignateContradiction(bottom);
            Assert.Equal(bottom, store.Schema.Contradiction);
            Assert.True(store.Schema[bottom]!.IsContradiction);
            Assert.False(store.Schema[f]!.IsContradiction);
            Assert.Throws<InvalidOperationException>(() => store.DesignateContradiction(bottom));
        }

    }

}

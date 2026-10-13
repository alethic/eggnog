using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Xunit;

using static Eggnog.Relations.Tests.LayeredTestUtil;
using static Eggnog.Relations.Tests.QueryTestUtil;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// Tests for <see cref="Store"/>: the bridge's layout (union-find table 0, id counter 0, clock counter 1 starting
    /// at 1), definitions at any time, sealed counters, the clock's limit, and the schema through which children reach
    /// root tables: it knows every root table, is never torn while tables are added, and freezes a table added while
    /// the root freezes.
    /// </summary>
    public class StoreTests
    {

        /// <summary>
        /// A function table <c>f(id) -&gt; id, ts</c> merged by union.
        /// </summary>
        internal static SortedWritesTable Function(Store store, int keys = 1) =>
            new(keys, keys + 2, new ColumnId(keys + 1), Enumerable.Range(0, keys + 1).Select(c => new ColumnId(c)).ToArray(), new UnionIdMerge(new ColumnId(keys), store.UnionFindTable, new ColumnId(keys + 1)), ParallelHeuristics.Serial);

        /// <summary>
        /// A new store lays its root database out as egglog's bridge does: the union-find is table 0, the id counter is
        /// counter 0 and the clock counter 1, starting at 1.
        /// </summary>
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

        /// <summary>
        /// A table the store adds is registered with the schema with its shape, stratum and privacy.
        /// </summary>
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

        /// <summary>
        /// The store refuses a table it cannot layer, and the schema refuses to register a table twice or the
        /// union-find.
        /// </summary>
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
        /// Every table of a store is added through <see cref="Store.AddTable"/>, so its schema, which children read and
        /// the root's rebuild walks, knows every root table. The root database's own <see cref="Database.AddTable"/> is
        /// refused, children or not; a bare database and a clone, which belong to no store, add tables as egglog does.
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

        /// <summary>
        /// A table added while children live is visible to them at once, and one added after they are disposed gets its
        /// stratum.
        /// </summary>
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

        /// <summary>
        /// Adding a table while another thread holds the root's domain throws and adds nothing; once the domain is free
        /// it succeeds.
        /// </summary>
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

        /// <summary>
        /// Counters may be added until the first child is created; from then on every way of adding one throws, also
        /// after every child is disposed.
        /// </summary>
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
        /// The clock is checked and throws at <see cref="uint.MaxValue"/>, which is <see cref="Value.StaleRep"/>; the
        /// last epoch it hands out is one below. Every clock read of the store applies the bound:
        /// <see cref="Store.ReadClock"/>, <see cref="Context.BeginEpoch"/>, a child's creation stamp and the databases'
        /// timestamp defaults.
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

        /// <summary>
        /// Only a registered zero-key table can be designated the contradiction table, and only once.
        /// </summary>
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

        /// <summary>
        /// A table added while the root freezes on another thread is frozen with the root, or refused.
        /// <see cref="Store.AddTable"/> reads the root's frozen flag again once it holds the root's domain, so a freeze
        /// that completes between the first read and the enter cannot leave the table unfrozen and unsealed in a frozen
        /// root. <c>EGGNOG_ADDTABLE_RACE</c> sets the number of iterations (5000 by default).
        /// </summary>
        [Fact]
        public void ATableAddedWhileTheRootFreezesIsFrozenOrRefused()
        {
            var iterations = int.TryParse(Environment.GetEnvironmentVariable("EGGNOG_ADDTABLE_RACE"), out var n) && n > 0 ? n : 5000;
            var bad = 0;
            for (var i = 0; i < iterations && bad == 0; i++)
            {
                var store = new Store(ParallelHeuristics.Serial);
                store.AddTable(MaxFunction(1), name: "f");
                var root = store.Root;
                using var start = new Barrier(2);
                TableId? added = null;
                var freezer = Task.Run(() =>
                {
                    start.SignalAndWait();
                    try
                    {
                        root.Freeze();
                    }
                    catch (InvalidOperationException)
                    {
                    }
                });
                var adder = Task.Run(() =>
                {
                    start.SignalAndWait();
                    try
                    {
                        added = store.AddTable(MaxFunction(1), name: "g");
                    }
                    catch (InvalidOperationException)
                    {
                    }
                });

                Task.WaitAll(freezer, adder);
                if (root.IsFrozen && added is { } g && store.Schema[g]!.Root.IsShared == false)
                {
                    bad++;
                    Console.WriteLine($"iteration {i}: g={g} rootDbFrozen={root.Database.IsFrozen} fShared={store.Schema[new TableId(1)]!.Root.IsShared} gInRootDb={root.Database.TryGetTableInfo(g, out _)} rootViewFrozen={store.Schema.RootView(g).Layers[0]!.IsFrozen}");
                }
            }

            Assert.Equal(0, bad);
        }

        /// <summary>
        /// Children reach root tables only through the store's schema, and a child that asserts nothing sees what its
        /// parent sees. A table that the store's root database holds is known to the schema: adding one to the root
        /// database directly after children exist is refused or registered.
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
        /// The schema is read lock-free while a table is added, so a reader never sees a torn schema: every id listed
        /// in <see cref="LayeredSchema.FunctionTables"/> has a <see cref="LayeredTableSchema"/> when read right after.
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

        /// <summary>
        /// Children reach root tables only through the store's <see cref="LayeredSchema"/>, and the root's rebuild pass
        /// rebuilds <see cref="LayeredSchema.FunctionTables"/>. A table added through the root database's own
        /// <see cref="Database.AddTable"/> is therefore refused (definitions go through the store, as
        /// <see cref="Database.AddCounter"/> is refused once children exist) or registered with the schema.
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
        /// Tables may be added while other threads read the schema (children of a frozen root), and every registered id
        /// the schema lists has its <see cref="LayeredTableSchema"/>: a reader that finds an id in
        /// <see cref="LayeredSchema.FunctionTables"/> finds <c>schema[id]</c>.
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

using System;
using System.Collections.Generic;
using System.Linq;

using Xunit;

using static Eggnog.Relations.Tests.QueryTestUtil;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// Tests for a child context's database: definitions go through the store, schema questions come from the store's
    /// schema, ids come from the child's blocks while container ids stay shared, and staging reaches the child's
    /// union-find and creates the child's layer of a table on first use.
    /// </summary>
    public class ChildDatabaseTests
    {

        static (Store Store, TableId F, Context Child) Setup()
        {
            var store = new Store(ParallelHeuristics.Serial);
            var f = store.AddTable(StoreTests.Function(store), [], [store.UnionFindTable], "f");
            return (store, f, store.Root.CreateChild());
        }

        /// <summary>
        /// A child's database refuses to define tables, counters and external functions, which only the store adds, so
        /// every context shares one schema.
        /// </summary>
        [Fact]
        public void DefinitionsGoThroughTheStore()
        {
            var (_, f, child) = Setup();
            var db = child.Database;
            Assert.True(db.IsChild);
            Assert.False(child.Store.Root.Database.IsChild);

            void Throws(Action action) => Assert.Equal("Definitions are added through the Store.", Assert.Throws<InvalidOperationException>(action).Message);
            Throws(() => db.AddTable(new SortedWritesTable(1, 2, null, [], new KeepCurrentMerge())));
            Throws(() => db.AddCounter());
            Throws(() => db.AddExternalFunction(Function(_ => 1)));
            Throws(() => db.FreeExternalFunction(new ExternalFunctionId(0)));
            Throws(() => _ = db.NextTableId);
            Assert.Equal(new TableId(2), child.Store.Root.Database.NextTableId);
            Assert.Equal(f, child.Store.Schema.FunctionTables[0]);
        }

        /// <summary>
        /// A child's database answers table counts, specs, names, id columns and arities from the store's schema,
        /// tables added afterwards included, while its own table map holds only its union-find and the layers it
        /// created.
        /// </summary>
        [Fact]
        public void SchemaQuestionsAreAnsweredFromTheStoresSchema()
        {
            var (store, f, child) = Setup();
            var db = child.Database;
            var uf = store.UnionFindTable;

            Assert.Equal(2, db.TableCount);
            Assert.Equal(new[] { uf, f }, db.ReadOnlyView.TableIds);
            Assert.Equal(new[] { uf, f }, db.CreateExecutionState().TableIds);
            Assert.Same(store.Root.Database.GetTableInfo(f).Spec, db.GetTableSpec(f));
            Assert.Same(DisplacedTable.DisplacedSpec, db.GetTableSpec(uf));
            Assert.Equal("f", db.GetTableName(f));
            Assert.Equal("f", db.ReadOnlyView.GetTableName(f));
            Assert.Equal("$uf", db.GetTableName(uf));
            Assert.Equal(new[] { C(0), C(1) }, db.GetIdColumns(f).ToArray());
            Assert.True(db.GetIdColumns(uf).IsEmpty);
            Assert.Equal(3, ((IQuerySchema)db).GetArity(f));
            Assert.Equal(1, ((IQuerySchema)db).GetKeyCount(f));
            Assert.Throws<KeyNotFoundException>(() => db.GetTableSpec(new TableId(5)));
            Assert.Throws<KeyNotFoundException>(() => db.GetIdColumns(new TableId(5)));

            // The child's own map holds only its union-find and the layers it created.
            Assert.Equal("$uf", db.GetTableInfo(uf).Name);
            Assert.NotSame(store.RootUnionFind, db.GetTable(uf));
            var missing = Assert.Throws<KeyNotFoundException>(() => db.GetTableInfo(f));
            Assert.Contains("has no layer in this context", missing.Message);
            Assert.False(db.TryGetTableInfo(f, out _));

            // A child's tables are layered: the view cannot hand one out as a single table.
            Assert.Throws<InvalidOperationException>(() => db.ReadOnlyView.GetTable(f));
            Assert.Throws<InvalidOperationException>(() => db.CreateExecutionState().GetTable(uf));

            // Tables added later are visible to the child at once.
            var g = store.AddTable(StoreTests.Function(store), name: "g");
            Assert.Equal(3, db.TableCount);
            Assert.Equal("g", db.GetTableName(g));
        }

        /// <summary>
        /// The first staging into a table creates the child's layer of it, once; the union-find id resolves to the
        /// child's own union-find; the root stages into its own tables.
        /// </summary>
        [Fact]
        public void StagingIntoAChildCreatesItsLayerOfTheTable()
        {
            var (store, f, child) = Setup();
            var db = child.Database;

            // The first staging into a table creates the child's layer, published to the catalog and adopted into the
            // table map; the layer is an ordinary single-shard table of the same shape.
            Assert.Null(child.Tables!.TryGetLayer(f));
            Assert.False(db.TryGetTableInfo(f, out _));
            var state = db.CreateExecutionState();
            state.StageInsert(f, [1, 2, 3]);
            var layer = child.Tables.TryGetLayer(f);
            Assert.NotNull(layer);
            Assert.True(layer!.IsAdopted);
            Assert.Same(layer.Own, db.Catalog.GetForStaging(f));
            Assert.Same(layer.Info, db.GetTableInfo(f));
            Assert.Same(layer, layer.Own.Layer);
            Assert.Equal(1, layer.Own.Parallelism.ShardData.Count);
            Assert.Same(child.Gate, layer.Own.CompactionGate);
            Assert.Equal(1, child.Tables.Statistics.LayersCreated);
            state.Flush();
            Assert.True(db.MergeAll(1));
            Assert.Equal(1, layer.Own.Count);
            Assert.Equal(1, child.Version);

            // A buffer from the database stages into the same layer; no second layer is created.
            db.NewBuffer(f).Flush();
            Assert.Same(layer, child.Tables.TryGetLayer(f));
            Assert.Equal(1, child.Tables.Statistics.LayersCreated);
            Assert.False(db.MergeAll(1));

            // The union-find id resolves to the child's own union-find through the resolver (the child's catalog
            // never holds it), so it is found even while a merge has it taken out of the table map.
            Assert.False(db.Catalog.TryGetTable(store.UnionFindTable, out _));
            Assert.Same(child.UnionFindTable, db.Catalog.GetForStaging(store.UnionFindTable));
            Assert.IsType<OverlayDisplacedTable>(db.GetTable(store.UnionFindTable));
            db.NewBuffer(store.UnionFindTable).Flush();
            Assert.False(db.Catalog.Notifications.IsEmpty);
            Assert.False(db.MergeAll(1));

            // The root stages into its own tables, and its catalog has no resolver.
            Assert.Null(store.Root.Database.Catalog.Resolver);
            Assert.NotNull(db.Catalog.Resolver);
            Assert.Throws<KeyNotFoundException>(() => store.Root.Database.NewBuffer(new TableId(9)));
        }

        /// <summary>
        /// Context = copy: the child's entry for the union-find is its own union-find layer (its own displacements
        /// only), not the union-find it sees, so no query member answers from it. The union-find is never a query atom
        /// in a child; headers are never evaluated as one table there; and a rule set built on another database's view
        /// is refused. The root's query members answer from its tables.
        /// </summary>
        [Fact]
        public void AChildAnswersNoQueryFromItsOwnLayersAsIfTheyWereItsView()
        {
            var (store, f, child) = Setup();
            var root = store.Root;
            var uf = store.UnionFindTable;
            var db = child.Database;

            // The root has a union the child must not be told is absent.
            var a = root.NewId();
            var b = root.NewId();
            root.Database.WithExecutionState(s =>
            {
                s.StageInsert(uf, [b, a, s.Timestamp]);
                return 0;
            });
            root.Database.MergeAll();
            Assert.Equal(1, store.RootUnionFind.UnionFind.Count);

            static RuleSet OverUnionFind(Database database, TableId uf)
            {
                var rsb = database.NewRuleSet();
                var q = rsb.NewRule();
                var x = q.NewVariable("x");
                var y = q.NewVariable("y");
                var t = q.NewVariable("t");
                q.AddAtom(uf, [x, y, t]);
                q.Build().Build("over-uf");
                return rsb.Build();
            }

            // The union-find is not a query atom in a child, for planning, statistics or estimates: only its lookups
            // reach a child's union-find.
            var fast = new List<Constraint>();
            var slow = new List<Constraint>();
            Assert.Throws<NotSupportedException>(() => OverUnionFind(db, uf));
            Assert.Throws<NotSupportedException>(() => ((IQuerySchema)db).SplitConstraints(uf, [], fast, slow));
            Assert.Throws<NotSupportedException>(() => db.ProcessConstraints(uf, []));
            Assert.Throws<NotSupportedException>(() => db.EstimateSize(uf));
            Assert.Throws<NotSupportedException>(() => new ColumnCardEstimator(db).GetColumnUniqueness(uf, C(0)));

            // Function tables are answered through the child's view (sum over the view's layers); never from the
            // child's own layer alone, so the single-table members refuse them.
            Assert.Equal(0, db.EstimateSize(f));
            Assert.Throws<NotSupportedException>(() => db.ProcessConstraints(f, []));
            Assert.Throws<NotSupportedException>(() => new ColumnCardEstimator(db).GetColumnUniqueness(f, C(0)));

            // A child never evaluates a header as a single table.
            Assert.Throws<InvalidOperationException>(() => ((IHeaderEvaluator)db).Evaluate(uf, [], fast, slow));
            Assert.Throws<InvalidOperationException>(() => ((IHeaderEvaluator)db).Evaluate(f, [], fast, slow));

            // A rule set built at the root is not run against the child's tables (it was built on another view); the
            // child runs its own.
            using (var rootRules = OverUnionFind(root.Database, uf))
            {
                Assert.Throws<ArgumentException>(() => db.RunRuleSet(rootRules));
                Assert.Equal(1L, root.Database.RunRuleSet(rootRules, ReportLevel.WithPlan).NumMatches("over-uf"));
            }

            using (var empty = db.NewRuleSet().Build())
                Assert.Same(RuleSetReport.Empty, db.RunRuleSet(empty));

            // The root's query members answer from its tables.
            Assert.Equal(1, root.Database.EstimateSize(uf));
            var evaluated = ((IHeaderEvaluator)root.Database).Evaluate(uf, [], fast, slow);
            Assert.Equal(1, evaluated.Size);
            evaluated.Dispose();
            using (var processed = root.Database.ProcessConstraints(uf, []))
                Assert.Equal(1, processed.Rows.Size);
        }

        /// <summary>
        /// A catalog's resolver is asked only for ids the catalog does not hold; without a resolver a missing id
        /// throws.
        /// </summary>
        [Fact]
        public void TheCatalogResolvesOnlyWhatItDoesNotHold()
        {
            var resolved = new List<TableId>();
            var fallback = new SortedWritesTable(1, 2, null, [], new KeepCurrentMerge());
            var catalog = new TableCatalog
            {
                Resolver = id =>
                {
                    resolved.Add(id);
                    return fallback;
                },
            };

            var own = new SortedWritesTable(1, 2, null, [], new KeepCurrentMerge());
            var id = catalog.Add(own);
            Assert.Same(own, catalog.GetForStaging(id));
            Assert.Same(fallback, catalog.GetForStaging(new TableId(4)));
            Assert.Equal(new[] { new TableId(4) }, resolved);
            Assert.Throws<KeyNotFoundException>(() => new TableCatalog().GetForStaging(new TableId(0)));
        }

        /// <summary>
        /// A child's database refuses the database-level rebuild entry points, since a child rebuilds through its
        /// context; the root's still run.
        /// </summary>
        [Fact]
        public void RebuildEntryPointsAreTheContextsInAChild()
        {
            var (store, f, child) = Setup();
            var db = child.Database;
            var uf = store.UnionFindTable;

            Assert.Throws<InvalidOperationException>(() => db.ApplyRebuild(uf, [f], 1));
            Assert.Throws<InvalidOperationException>(() => db.RefreshRowsForValues([f], [1u], 1));
            Assert.Throws<InvalidOperationException>(() => db.RebuildContainers(uf));
            Assert.Throws<InvalidOperationException>(() => db.RebuildPass(uf, [f]));
            Assert.Throws<InvalidOperationException>(() => db.Rebuild(uf, [f], store.ClockCounter));

            // The root's entry points run.
            Assert.False(store.Root.Database.RebuildPass(uf, [f]));
        }

        /// <summary>
        /// Every way a child mints a term id (directly, through its database, a state or a cloned state) draws from the
        /// child's own id blocks, so its ids are local to it; root mints, shared mints and other counters are not
        /// redirected.
        /// </summary>
        [Fact]
        public void AChildMintsTermIdsFromItsOwnBlocks()
        {
            var store = new Store();
            var extra = store.AddCounter();
            var root = store.Root;
            var child = root.CreateChild();
            var sibling = root.CreateChild();
            var ids = store.IdCounter;

            Assert.Throws<InvalidOperationException>(() => root.IsLocalId(0));

            var direct = child.NewId();
            var viaDatabase = (uint)child.Database.IncrementCounter(ids);
            var state = child.Database.CreateExecutionState();
            var viaState = (uint)state.IncrementCounter(ids);
            var viaClone = (uint)state.Clone().IncrementCounter(ids);
            Assert.All(new[] { direct, viaDatabase, viaState, viaClone }, id => Assert.True(child.IsLocalId(id)));
            Assert.All(new[] { direct, viaDatabase, viaState, viaClone }, id => Assert.False(sibling.IsLocalId(id)));
            Assert.Equal(4, new[] { direct, viaDatabase, viaState, viaClone }.Distinct().Count());
            Assert.NotNull(child.IdBlocks);
            Assert.Null(root.IdBlocks);
            Assert.Equal(4, child.IdBlocks!.Minted);

            // Root mints, shared mints and other counters are not redirected.
            var rootId = root.NewId();
            Assert.False(child.IsLocalId(rootId));
            var shared = (uint)state.IncrementSharedCounter(ids);
            Assert.False(child.IsLocalId(shared));
            Assert.Equal(0, child.Database.IncrementCounter(extra));
            Assert.Equal(1, root.Database.IncrementCounter(extra));
            Assert.Equal(2, state.IncrementCounter(extra));

            // A sibling's ids are its own.
            var siblingId = sibling.NewId();
            Assert.True(sibling.IsLocalId(siblingId));
            Assert.False(child.IsLocalId(siblingId));
        }

        /// <summary>
        /// A container over base values a child interns gets its id from the shared counter, not the child's blocks: such
        /// container ids are global, so the root interning the same container finds the same id. A container of classes
        /// a writable child interns in its own layers, under an id local to it.
        /// </summary>
        [Fact]
        public void ContainerIdsAreMintedOnTheSharedCounterInAChild()
        {
            var store = new Store();
            var root = store.Root;
            // A container over base values (holdsIds: false), which every context interns in the root's environment; one
            // that holds ids a writable child interns in its own layers.
            root.Database.Containers.RegisterType<VecContainer>(store.IdCounter, new UnionContainerMerge(store.UnionFindTable, store.ClockCounter), holdsIds: false);
            var child = root.CreateChild();
            Assert.Same(root.Database.Containers, child.Database.Containers);

            var container = child.Database.WithExecutionState(s => s.Containers.GetOrInsert(new VecContainer(1, 2, 3), s));
            Assert.False(child.IsLocalId(container));
            var term = child.NewId();
            Assert.True(child.IsLocalId(term));

            // The same container interned at the root is the same id: it is global.
            Assert.Equal(container, root.Database.WithExecutionState(s => s.Containers.GetOrInsert(new VecContainer(1, 2, 3), s)));
        }

        /// <summary>
        /// A counter value equal to the stale-row marker is rejected when a row would be written with it.
        /// </summary>
        [Fact]
        public void ACounterValueAtTheStaleMarkerIsRejected()
        {
            // egglog's from_usize accepts u32::MAX, the stale-row marker (numeric-id/src/lib.rs:528-531); here a
            // counter value at the marker throws.
            var db = new Database();
            var c = db.AddCounter();
            var t = db.AddTable(new SortedWritesTable(1, 2, null, [], new KeepCurrentMerge()));
            db.Counters.IncrementBy(c, Value.StaleRep - 1L);
            var state = db.CreateExecutionState();
            Assert.Equal(Value.StaleRep - 1, state.PredictRow(t, [1], [MergeVal.FromCounter(c)])[1]);
            Assert.Throws<OverflowException>(() => state.PredictRow(t, [2], [MergeVal.FromCounter(c)]));
        }

        /// <summary>
        /// The root mints ids up to the union-find's maximum and refuses the next, and so does a child created then.
        /// </summary>
        [Fact]
        public void TheRootRefusesNewIdsPastTheUnionFindMaximum()
        {
            var store = new Store();
            store.Counters.IncrementBy(store.IdCounter, UnionFind.MaxId);
            Assert.Equal(UnionFind.MaxId, store.Root.NewId());
            Assert.Throws<OverflowException>(() => store.Root.NewId());
            var child = store.Root.CreateChild();
            Assert.Throws<OverflowException>(() => child.NewId());
        }

    }

}

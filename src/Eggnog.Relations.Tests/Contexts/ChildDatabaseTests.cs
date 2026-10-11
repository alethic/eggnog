using System;
using System.Collections.Generic;
using System.Linq;

using Xunit;

using static Eggnog.Relations.Tests.QueryTestUtil;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// Tests for a child context's database as far as it exists without layers (colored-contexts spec §3.4, §4.2,
    /// §17 M9.1): definitions go through the store, schema questions come from the store's schema, ids come from the
    /// child's blocks while container ids stay shared, and staging reaches only the union-find until the layers arrive
    /// (interim).
    /// </summary>
    public class ChildDatabaseTests
    {

        static (Store Store, TableId F, Context Child) Setup()
        {
            var store = new Store(ParallelHeuristics.Serial);
            var f = store.AddTable(StoreTests.Function(store), [], [store.UnionFindTable], "f");
            return (store, f, store.Root.CreateChild());
        }

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

            // The child's own map holds only its union-find (and, from M9.3, its layers).
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

        [Fact]
        public void StagingIntoAChildReachesOnlyItsUnionFindBeforeItsLayersExist()
        {
            var (store, f, child) = Setup();
            var db = child.Database;

            Assert.Equal("Layers arrive in M9.3.", Assert.Throws<NotSupportedException>(() => db.NewBuffer(f)).Message);
            var state = db.CreateExecutionState();
            Assert.Equal("Layers arrive in M9.3.", Assert.Throws<NotSupportedException>(() => state.StageInsert(f, [1, 2, 3])).Message);
            Assert.Throws<NotSupportedException>(() => db.ClearTable(f));
            Assert.True(db.Catalog.Notifications.IsEmpty);
            Assert.False(db.MergeAll());
            Assert.Equal(0, child.Version);

            // M9.2: the union-find id resolves to the child's own union-find through the resolver (the child's catalog
            // holds no table), so it is found even while a merge has it taken out of the table map.
            Assert.False(db.Catalog.TryGetTable(store.UnionFindTable, out _));
            Assert.Same(child.UnionFindTable, db.Catalog.GetForStaging(store.UnionFindTable));
            Assert.IsType<OverlayDisplacedTable>(db.GetTable(store.UnionFindTable));
            db.NewBuffer(store.UnionFindTable).Flush();
            Assert.False(db.Catalog.Notifications.IsEmpty);
            Assert.False(db.MergeAll(1));

            // The root stages as before, and its catalog has no resolver.
            Assert.Null(store.Root.Database.Catalog.Resolver);
            Assert.NotNull(db.Catalog.Resolver);
            Assert.Throws<KeyNotFoundException>(() => store.Root.Database.NewBuffer(new TableId(9)));
        }

        /// <summary>
        /// Context = copy (§1, §3.4, X23): the child's entry for the union-find is its own union-find layer (its own
        /// displacements only, from M9.2), not the union-find it sees, so no query member answers from it. The
        /// union-find is never a query atom in a child; headers are never evaluated as one table there; and rules do not
        /// run in a child before its views exist (M10.2). The root's query members are unchanged.
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

            // X23: the union-find is not a query atom in a child, for planning, statistics or estimates.
            var fast = new List<Constraint>();
            var slow = new List<Constraint>();
            Assert.Throws<NotSupportedException>(() => OverUnionFind(db, uf));
            Assert.Throws<NotSupportedException>(() => ((IQuerySchema)db).SplitConstraints(uf, [], fast, slow));
            Assert.Throws<NotSupportedException>(() => db.ProcessConstraints(uf, []));
            Assert.Throws<NotSupportedException>(() => db.EstimateSize(uf));
            Assert.Throws<NotSupportedException>(() => new ColumnCardEstimator(db).GetColumnUniqueness(uf, C(0)));

            // Function tables have no layer in the child before M9.3, and are read through views from M10.2.
            Assert.Throws<KeyNotFoundException>(() => db.EstimateSize(f));
            Assert.Throws<KeyNotFoundException>(() => db.ProcessConstraints(f, []));

            // §3.4: a child never evaluates a header as a single table.
            Assert.Throws<InvalidOperationException>(() => ((IHeaderEvaluator)db).Evaluate(uf, [], fast, slow));
            Assert.Throws<InvalidOperationException>(() => ((IHeaderEvaluator)db).Evaluate(f, [], fast, slow));

            // A rule set built at the root is not run against the child's tables, and no rule set runs in a child yet.
            using (var rootRules = OverUnionFind(root.Database, uf))
            {
                Assert.Equal("Rules in a child context arrive in M10.2.", Assert.Throws<NotSupportedException>(() => db.RunRuleSet(rootRules)).Message);
                Assert.Equal(1L, root.Database.RunRuleSet(rootRules, ReportLevel.WithPlan).NumMatches("over-uf"));
            }

            using (var empty = db.NewRuleSet().Build())
                Assert.Throws<NotSupportedException>(() => db.RunRuleSet(empty));

            // The root's query members answer as before.
            Assert.Equal(1, root.Database.EstimateSize(uf));
            var evaluated = ((IHeaderEvaluator)root.Database).Evaluate(uf, [], fast, slow);
            Assert.Equal(1, evaluated.Size);
            evaluated.Dispose();
            using (var processed = root.Database.ProcessConstraints(uf, []))
                Assert.Equal(1, processed.Rows.Size);
        }

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

            // The root's entry points are unchanged.
            Assert.False(store.Root.Database.RebuildPass(uf, [f]));
        }

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

        [Fact]
        public void ContainerIdsAreMintedOnTheSharedCounterInAChild()
        {
            var store = new Store();
            var root = store.Root;
            root.Database.Containers.RegisterType<VecContainer>(store.IdCounter, new UnionContainerMerge(store.UnionFindTable, store.ClockCounter));
            var child = root.CreateChild();
            Assert.Same(root.Database.Containers, child.Database.Containers);

            var container = child.Database.WithExecutionState(s => s.Containers.RegisterValue(new VecContainer(1, 2, 3), s));
            Assert.False(child.IsLocalId(container));
            var term = child.NewId();
            Assert.True(child.IsLocalId(term));

            // The same container interned at the root is the same id: it is global.
            Assert.Equal(container, root.Database.WithExecutionState(s => s.Containers.RegisterValue(new VecContainer(1, 2, 3), s)));
        }

        [Fact]
        public void ACounterValueAtTheStaleMarkerIsRejected()
        {
            // Defect fixed (numeric-id/src/lib.rs:528-531): egglog's from_usize accepts u32::MAX, the stale-row marker.
            var db = new Database();
            var c = db.AddCounter();
            var t = db.AddTable(new SortedWritesTable(1, 2, null, [], new KeepCurrentMerge()));
            db.Counters.IncrementBy(c, Value.StaleRep - 1L);
            var state = db.CreateExecutionState();
            Assert.Equal(Value.StaleRep - 1, state.PredictRow(t, [1], [MergeVal.FromCounter(c)])[1]);
            Assert.Throws<OverflowException>(() => state.PredictRow(t, [2], [MergeVal.FromCounter(c)]));
        }

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

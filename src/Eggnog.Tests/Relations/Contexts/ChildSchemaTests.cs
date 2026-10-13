using System;
using System.Collections.Generic;
using System.Linq;

using Xunit;

using static Eggnog.Relations.Tests.LayeredTestUtil;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// Tests for the members of a child's database that need no planning, once the child has layers: schema questions
    /// come from the store's schema whatever the child stored, estimates sum the view's layers, buffers create layers,
    /// clears route to the context, and definitions go through the store.
    /// </summary>
    public class ChildSchemaTests
    {

        static (Store Store, TableId F, TableId V, Context Root, uint[] Ids) Setup()
        {
            var store = new Store(ParallelHeuristics.Serial);
            var f = store.AddTable(MaxFunction(1), name: "f");
            var v = store.AddTable(ValueOnly(), name: "v");
            var root = store.Root;
            var ids = Enumerable.Range(0, 5).Select(_ => root.NewId()).ToArray();
            for (var i = 0; i < 3; i++)
                Insert(root, f, ids[i], (uint)i, 0);

            return (store, f, v, root, ids);
        }

        /// <summary>
        /// With a layer of its own, a child still answers specs, names, id columns, arities and table ids from the
        /// store's schema; only its table map entry for the layered table is the layer's.
        /// </summary>
        [Fact]
        public void SchemaQuestionsIgnoreTheChildsLayers()
        {
            var (store, f, v, root, ids) = Setup();
            root.Freeze();
            var child = root.CreateChild();
            Insert(child, f, ids[4], 1, 0);
            var db = child.Database;

            Assert.Same(store.Schema[f]!.Spec, db.GetTableSpec(f));
            Assert.Same(DisplacedTable.DisplacedSpec, db.GetTableSpec(store.UnionFindTable));
            Assert.Equal("f", db.GetTableName(f));
            Assert.Equal("v", db.GetTableName(v));
            Assert.Equal("$uf", db.GetTableName(store.UnionFindTable));
            Assert.Equal(new[] { C(0) }, db.GetIdColumns(f).ToArray());
            Assert.True(db.GetIdColumns(v).IsEmpty);
            Assert.Equal(3, ((IQuerySchema)db).GetArity(f));
            Assert.Equal(1, ((IQuerySchema)db).GetKeyCount(f));
            Assert.Equal(store.Schema.TableCount, db.TableCount);
            Assert.Equal(new[] { store.UnionFindTable, f, v }, db.ReadOnlyView.TableIds);

            // The child's own table map holds its layer; its entry is the layer's, not the root's.
            Assert.Same(child.Tables!.TryGetLayer(f)!.Info, db.GetTableInfo(f));
            Assert.NotSame(store.Schema[f]!.RootInfo, db.GetTableInfo(f));
            Assert.Throws<KeyNotFoundException>(() => db.GetTableInfo(v));
        }

        /// <summary>
        /// A child's size estimates sum the slots of its view's layers, hidden ones included, and a constraint is
        /// estimated per layer; the union-find has no estimate in a child.
        /// </summary>
        [Fact]
        public void EstimatesSumTheViewsLayers()
        {
            var (store, f, v, root, ids) = Setup();
            root.Freeze();
            var child = root.CreateChild();

            // Untouched: the root's estimate.
            Assert.Equal(3, child.Database.EstimateSize(f));
            Assert.Equal(0, child.Database.EstimateSize(v));

            // Two own rows, one of which hides a root row: the estimate counts slots, hidden ones included.
            Insert(child, f, ids[0], 9, 0);
            Insert(child, f, ids[3], 1, 0);
            Assert.Equal(5, child.Database.EstimateSize(f));
            Assert.Equal(4, child.GetView(f).VisibleCount);

            // A constraint is estimated per layer (a slow one exactly).
            Assert.Equal(1, child.Database.EstimateSize(f, Constraint.EqConst(C(0), new Value(ids[3]))));
            Assert.Equal(2, child.Database.EstimateSize(f, Constraint.EqConst(C(0), new Value(ids[0]))));
            Assert.Throws<NotSupportedException>(() => child.Database.EstimateSize(store.UnionFindTable));
        }

        /// <summary>
        /// A buffer from a child's database creates the child's layer and a notification, and its rows merge; a frozen
        /// child's or root's database refuses buffers, merges, clears and runs.
        /// </summary>
        [Fact]
        public void BuffersCreateLayersAndNotify()
        {
            var (_, f, v, root, ids) = Setup();
            root.Freeze();
            var child = root.CreateChild();
            var db = child.Database;
            var buffer = db.NewBuffer(v);
            Assert.NotNull(child.Tables!.TryGetLayer(v));
            Assert.False(db.Catalog.Notifications.IsEmpty);
            buffer.StageInsert([7, 70]);
            buffer.Flush();
            Assert.True(db.MergeAll(child.Store.ReadClock()));
            Assert.Equal(70u, Row(child, v, 7)![1]);

            // A frozen child's database refuses new buffers.
            child.Freeze();
            Assert.Throws<InvalidOperationException>(() => db.NewBuffer(f));
            Assert.Throws<InvalidOperationException>(() => db.MergeAll());
            Assert.Throws<InvalidOperationException>(() => db.MergeTable(v));
            Assert.Throws<InvalidOperationException>(() => db.ClearTable(v));
            Assert.Throws<InvalidOperationException>(() => root.Database.NewBuffer(f));
            Assert.Throws<InvalidOperationException>(() => root.Database.ClearTable(f));
            using var rules = root.Database.NewRuleSet().Build();
            Assert.Throws<InvalidOperationException>(() => root.Database.RunRuleSet(rules));
        }

        /// <summary>
        /// A clear through a child's database is the context's clear: refused under a writable parent, and under a
        /// frozen one it hides every visible row of the table in the child only; clearing the union-find is refused.
        /// </summary>
        [Fact]
        public void ClearsRouteToTheContext()
        {
            var (_, f, _, root, ids) = Setup();

            // Under a writable parent the clear is refused (a child retracts only under a frozen parent) and changes
            // nothing.
            var refused = root.CreateChild();
            Assert.Throws<InvalidOperationException>(() => refused.Database.ClearTable(f));
            Assert.Null(refused.Tables!.TryGetLayer(f));
            refused.Dispose();

            root.Freeze();
            var child = root.CreateChild();
            Insert(child, f, ids[4], 4, 0);
            var version = child.Version;
            child.Database.ClearTable(f);
            Assert.True(child.Version > version);
            Assert.True(child.Tables!.TryGetLayer(f)!.IsCleared);
            Assert.Equal(0, child.GetView(f).VisibleCount);
            Assert.Equal(0, child.Database.EstimateSize(f));
            Assert.Equal(3, root.GetView(f).VisibleCount);
            Assert.Throws<InvalidOperationException>(() => child.ClearTable(child.Store.UnionFindTable));
            Assert.False(child.IsFaulted);
        }

        /// <summary>
        /// A child's database refuses definitions, which only the store adds; a table the store adds is visible to the
        /// child at once.
        /// </summary>
        [Fact]
        public void DefinitionsGoThroughTheStore()
        {
            var (store, _, _, root, _) = Setup();
            var child = root.CreateChild();
            var db = child.Database;
            void Throws(Action action) => Assert.Equal("Definitions are added through the Store.", Assert.Throws<InvalidOperationException>(action).Message);
            Throws(() => db.AddTable(MaxFunction(1)));
            Throws(() => db.AddCounter());
            Throws(() => db.AddExternalFunction(ActionTestUtil.Function((_, _) => 1)));
            Throws(() => db.FreeExternalFunction(new ExternalFunctionId(0)));
            Throws(() => _ = db.NextTableId);

            // A table added by the store is visible to the child at once.
            var g = store.AddTable(MaxFunction(1), name: "g");
            Assert.Equal("g", db.GetTableName(g));
            Insert(child, g, root.NewId(), 1, 0);
            Assert.NotNull(child.Tables!.TryGetLayer(g));
        }

    }

}

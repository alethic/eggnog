using System;
using System.Collections.Generic;
using System.Linq;

using Xunit;

using static Eggnog.Relations.Tests.LayeredTestUtil;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// Rules built and planned in a child context: a child whose database holds only its own layers answers schema
    /// questions from the store's schema and planning questions from its view, so <c>NewRule</c>, <c>AddAtom</c>,
    /// <c>Build</c> and <c>PlanQuery</c> work for tables it never wrote, with read-through statistics; the union-find
    /// is never an atom there: a child's union-find is read only through lookups, and a child does not rebuild with
    /// rules.
    /// </summary>
    public class ChildPlanningTests
    {

        static (Store Store, TableId F, TableId G, Context Root, uint[] Ids) Setup()
        {
            var store = new Store(ParallelHeuristics.Serial);
            var f = store.AddTable(MaxFunction(1), name: "f");
            var g = store.AddTable(Relation(2), name: "g");
            var root = store.Root;
            var ids = Enumerable.Range(0, 20).Select(_ => root.NewId()).ToArray();
            root.Write(state =>
            {
                for (var i = 0; i < ids.Length; i++)
                {
                    state.StageInsert(f, [ids[i], (uint)i, state.Timestamp]);
                    if (i % 2 == 0)
                        state.StageInsert(g, [ids[i], ids[(i + 1) % ids.Length], state.Timestamp]);
                }

                return 0;
            });

            return (store, f, g, root, ids);
        }

        [Fact]
        public void ARuleIsBuiltPlannedAndRunInAChildThatNeverWroteItsTables()
        {
            var (store, f, g, root, ids) = Setup();
            using var child = root.CreateChild();
            var db = child.Database;
            Assert.Null(child.Tables!.TryGetLayer(f));
            Assert.Null(child.Tables!.TryGetLayer(g));

            var log = new List<uint[]>();
            var recorder = store.Root.Database.AddExternalFunction(ExecutionTestUtil.Recorder(log));
            var rsb = db.NewRuleSet();
            var q = rsb.NewRule();
            var x = q.NewVariable("x");
            var y = q.NewVariable("y");
            var v = q.NewVariable("v");
            var a0 = q.AddAtom(g, [x, y, QueryEntry.Wildcard()]);
            q.AddAtom(f, [x, v, QueryEntry.Wildcard()]);
            var rb = q.Build();
            rb.CallExternal(recorder, [x, y, v]);
            var rule = rb.Build("g and f");
            using var rs = rsb.Build();

            // SymbolMap names come from the store's schema; nothing created a layer.
            Assert.True(rs[rule].Symbols.TryGetAtomName(a0, out var name));
            Assert.Equal("g", name);
            Assert.True(rs[rule].Symbols.TryGetVariableName(x, out var xName));
            Assert.Equal("x", xName);
            Assert.Null(child.Tables!.TryGetLayer(f));
            Assert.Null(child.Tables!.TryGetLayer(g));

            // A child that writes nothing sees what its parent sees: the child's run sees what the root sees.
            db.RunRuleSet(rs, ReportLevel.TimeOnly, store.ReadClock());
            var expected = Enumerable.Range(0, ids.Length).Where(i => i % 2 == 0).Select(i => $"{ids[i]},{ids[(i + 1) % ids.Length]},{i}");
            Assert.Equal(expected.OrderBy(s => s, StringComparer.Ordinal), ExecutionTestUtil.Distinct(log));
        }

        [Fact]
        public void PlanQueryReadsTheChildsView()
        {
            var (_, f, g, root, ids) = Setup();
            using var child = root.CreateChild();
            var db = child.Database;
            var builder = new QueryBuilder(db);
            var x = builder.NewVariable("x");
            var y = builder.NewVariable("y");
            builder.AddAtom(f, [x, y, QueryEntry.Wildcard()]);
            builder.AddAtom(g, [x, QueryEntry.Wildcard(), QueryEntry.Wildcard()]);
            var query = builder.Build(new ActionId(0));
            var template = db.PlanQuery(query);
            Assert.Equal(2, template.Atoms.Count);

            // The atoms' statistics are the view's: the root's rows, read through, then the child's own as well.
            IPlanStatistics stats = db;
            var fAtom = query.Atoms[new AtomId(0)];
            Assert.Equal(ids.Length, stats.GetApproximateSize(new AtomId(0), fAtom));
            Assert.Equal(new ColumnUniqueness((ulong)ids.Length, (ulong)ids.Length), stats.GetColumnUniqueness(f, C(0)));
            Assert.Equal(ids.Length, db.EstimateSize(f));

            var fresh = Enumerable.Range(0, 5).Select(_ => child.NewId()).ToArray();
            child.Write(state =>
            {
                foreach (var id in fresh)
                    state.StageInsert(f, [id, 7, state.Timestamp]);

                return 0;
            });

            Assert.Equal(ids.Length + fresh.Length, stats.GetApproximateSize(new AtomId(0), fAtom));
            Assert.Equal(ids.Length + fresh.Length, (int)stats.GetColumnUniqueness(f, C(0)).TableSize);

            // The distinct counts of the layers are summed too (every id is new in its layer).
            Assert.Equal(new ColumnUniqueness((ulong)(ids.Length + fresh.Length), (ulong)(ids.Length + fresh.Length)), stats.GetColumnUniqueness(f, C(0)));
            Assert.Equal(ids.Length + fresh.Length, db.EstimateSize(f));

            // A header over the child's view has a subset per layer.
            var fast = new List<Constraint>();
            var slow = new List<Constraint>();
            var header = db.View.Evaluate(f, [Constraint.EqConst(C(1), new Value(7))], fast, slow);
            Assert.Equal(0b11u, header.LayerMask);
            Assert.Equal(fresh.Length, header.GetLayer(1).Size);
            Assert.Equal(1, header.GetLayer(0).Size);
            Assert.Single(fast);
            Assert.Empty(slow);
            header.Dispose(null);
        }

        [Fact]
        public void UnionFindAtomsAreRefusedInAChild()
        {
            var (store, _, _, root, _) = Setup();
            using var child = root.CreateChild();
            var rsb = child.Database.NewRuleSet();
            var q = rsb.NewRule();
            var a = q.NewVariable();
            var b = q.NewVariable();
            var t = q.NewVariable();
            Assert.Throws<NotSupportedException>(() => q.AddAtom(store.UnionFindTable, [a, b, t]));
            Assert.Throws<NotSupportedException>(() => child.Database.View.GetTableView(store.UnionFindTable));
            Assert.Throws<NotSupportedException>(() => child.Database.EstimateSize(store.UnionFindTable));
            Assert.Throws<NotSupportedException>(() => child.Database.View.Evaluate(store.UnionFindTable, [], new List<Constraint>(), new List<Constraint>()));

            // The root plans over its union-find as egglog does.
            var rootRules = root.Database.NewRuleSet().NewRule();
            var x = rootRules.NewVariable();
            rootRules.AddAtom(store.UnionFindTable, [x, rootRules.NewVariable(), rootRules.NewVariable()]);
        }

        [Fact]
        public void SchemaQuestionsComeFromTheStoreSchema()
        {
            var (store, f, g, root, _) = Setup();
            using var child = root.CreateChild();
            var db = child.Database;
            IQuerySchema schema = db;
            Assert.Equal(3, schema.GetArity(f));
            Assert.Equal(1, schema.GetKeyCount(f));
            Assert.Equal(2, schema.GetKeyCount(g));
            Assert.Equal("f", db.GetTableName(f));
            Assert.True(schema.IsCacheable(f, C(0)));

            // Splitting classifies on the root's entry without building an index there.
            var rootInfo = store.Schema[g]!.RootInfo;
            var before = rootInfo.ColumnIndexes.Count;
            var fast = new List<Constraint>();
            var slow = new List<Constraint>();
            schema.SplitConstraints(g, [Constraint.EqConst(C(1), new Value(3)), Constraint.GeConst(C(2), new Value(1)), Constraint.Eq(C(0), C(1))], fast, slow);
            Assert.Equal([Constraint.GeConst(C(2), new Value(1)), Constraint.EqConst(C(1), new Value(3))], fast);
            Assert.Equal([Constraint.Eq(C(0), C(1))], slow);
            Assert.Equal(before, rootInfo.ColumnIndexes.Count);
        }

        [Fact]
        public void PlanningRequiresCurrentViews()
        {
            var (_, f, _, root, ids) = Setup();
            using var child = root.CreateChild();

            // The parent changes after the child was created: the child's views are stale until it catches up.
            Insert(root, f, root.NewId(), 3, 0);
            Assert.False(child.IsViewCurrent);
            var fast = new List<Constraint>();
            var slow = new List<Constraint>();
            Assert.Throws<InvalidOperationException>(() => child.Database.View.Evaluate(f, [], fast, slow));
            child.Rebuild();
            var all = child.Database.View.Evaluate(f, [], fast, slow);
            Assert.Equal(ids.Length + 1, all.Size);
            all.Dispose(null);
        }

    }

}

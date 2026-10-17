using System;
using System.Collections.Generic;
using System.Linq;

using Xunit;

using static Eggnog.Relations.Tests.LayeredTestUtil;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// Header evaluation per layer. A context's view evaluates a header's fast constraints over each present layer's
    /// own entry and indexes (a time range is one binary search per layer); a header is empty iff it is empty in every
    /// layer, and then the variant is dropped before an action id is taken; the single-table
    /// <see cref="PlanTemplate.Instantiate{TEvaluator}"/> puts everything in layer 0.
    /// </summary>
    public class LayeredHeaderTests
    {

        /// <summary>
        /// A chain root → middle → leaf, each writing rows of <c>f(k) → v</c> at its own epochs (so each layer's time
        /// column lies in its own range), the root and the middle frozen.
        /// </summary>
        static (Store Store, TableId F, Context Leaf, uint[] Epochs) Chain()
        {
            var store = new Store(ParallelHeuristics.Serial);
            var f = store.AddTable(MaxFunction(1), name: "f");
            var root = store.Root;
            var ids = Enumerable.Range(0, 30).Select(_ => root.NewId()).ToArray();
            var epochs = new uint[3];
            epochs[0] = Write(root, f, ids[..10], 1);
            root.Freeze();
            var middle = root.CreateChild();
            epochs[1] = Write(middle, f, ids[10..20], 2);
            middle.Freeze();
            var leaf = middle.CreateChild();
            epochs[2] = Write(leaf, f, ids[20..], 3);
            return (store, f, leaf, epochs);
        }

        static uint Write(Context context, TableId f, uint[] keys, uint value)
        {
            return context.Write(state =>
            {
                foreach (var key in keys)
                    state.StageInsert(f, [key, value, state.Timestamp]);

                return state.Timestamp;
            });
        }

        [Fact]
        public void ATimeRangeIsOneSearchPerLayer()
        {
            var (_, f, leaf, epochs) = Chain();
            var view = leaf.Database.View;
            var fast = new List<Constraint>();
            var slow = new List<Constraint>();
            var subset = view.Evaluate(f, [Constraint.GeConst(new ColumnId(2), new Value(epochs[1]))], fast, slow);
            Assert.Equal(0b110u, subset.LayerMask);
            Assert.Equal(10, subset.GetLayer(1).Size);
            Assert.Equal(10, subset.GetLayer(2).Size);
            Assert.True(subset.GetLayer(1).IsDense && subset.GetLayer(2).IsDense);
            Assert.Single(fast);
            Assert.Empty(slow);
            subset.Dispose(null);

            fast.Clear();
            var old = view.Evaluate(f, [Constraint.LtConst(new ColumnId(2), new Value(epochs[1]))], fast, slow);
            Assert.Equal(0b001u, old.LayerMask);
            Assert.False(old.IsMultiLayer);
            Assert.Equal(10, old.Size);
            old.Dispose(null);
        }

        /// <summary>
        /// Header evaluation with scratch moves every layer's rows out of it, whether they span several layers or
        /// collapse to one: a header over <c>g</c>, which has no middle layer, must not see the middle rows of the header
        /// over <c>f</c> before it.
        /// </summary>
        [Fact]
        public void ScratchKeepsNoRowsFromOneHeaderToTheNext()
        {
            var store = new Store(ParallelHeuristics.Serial);
            var f = store.AddTable(MaxFunction(1), name: "f");
            var g = store.AddTable(MaxFunction(1), name: "g");
            var root = store.Root;
            var ids = Enumerable.Range(0, 30).Select(_ => root.NewId()).ToArray();
            var first = Write(root, f, ids[..10], 1);
            Write(root, g, ids[..10], 1);
            root.Freeze();
            var middle = root.CreateChild();
            var second = Write(middle, f, ids[10..20], 2);
            middle.Freeze();
            var leaf = middle.CreateChild();
            var third = Write(leaf, f, ids[20..], 3);
            Write(leaf, g, ids[20..], 3);
            Assert.Equal(0b101u, leaf.GetView(g).PresentMask);

            var view = leaf.Database.View;
            var scratch = new HeaderScratch();
            var fast = new List<Constraint>();
            var slow = new List<Constraint>();
            Constraint[] all = [Constraint.GeConst(new ColumnId(2), new Value(first))];
            Constraint[] newer = [Constraint.GeConst(new ColumnId(2), new Value(second))];
            Constraint[] middleOnly = [Constraint.GeConst(new ColumnId(2), new Value(second)), Constraint.LtConst(new ColumnId(2), new Value(third))];
            foreach (var (constraints, mask, size) in new[] { (newer, 0b110u, 20), (middleOnly, 0b010u, 10) })
            {
                fast.Clear();
                var fRows = view.Evaluate(f, constraints, fast, slow, scratch);
                Assert.Equal(mask, fRows.LayerMask);
                Assert.Equal(size, fRows.Size);

                fast.Clear();
                var gRows = view.Evaluate(g, all, fast, slow, scratch);
                Assert.Equal(0b101u, gRows.LayerMask);
                Assert.Equal(20, gRows.Size);
                fRows.Dispose(null);
                gRows.Dispose(null);
            }
        }

        [Fact]
        public void AnEqualityUsesEveryLayersOwnIndex()
        {
            var (store, f, leaf, _) = Chain();
            var fast = new List<Constraint>();
            var slow = new List<Constraint>();
            var subset = leaf.Database.View.Evaluate(f, [Constraint.EqConst(new ColumnId(1), new Value(2))], fast, slow);
            Assert.Equal(0b010u, subset.LayerMask);
            Assert.Equal(10, subset.Size);
            subset.Dispose(null);

            // The equality was promoted through each present layer's own catalog: the root's and the middle's frozen
            // entries, the leaf's own.
            Assert.True(store.Schema[f]!.RootInfo.ColumnIndexes.Count > 0);
            foreach (var layer in leaf.GetView(f).Layers)
                Assert.Contains(layer!.Info.ColumnIndexes.Entries.ToArray(), e => e.Key.SequenceEqual([new ColumnId(1)]) && e.Index.IsCompleted);
        }

        [Fact]
        public void AHeaderEmptyInEveryLayerDropsTheVariantBeforeAnActionId()
        {
            var (store, f, leaf, epochs) = Chain();
            var rsb = leaf.Database.NewRuleSet();
            var q = rsb.NewRule();
            var k = q.NewVariable("k");
            q.AddAtom(f, [k, QueryEntry.Wildcard(), QueryEntry.Wildcard()]);
            var rb = q.Build();
            rb.AssertEq(k, k);
            var rule = rb.Build("all");
            using var built = rsb.Build();
            var cached = built.BuildCachedPlan(rule);

            var next = leaf.Database.NewRuleSet();
            Assert.Null(next.AddRuleFromCachedPlan(cached, [(new AtomId(0), Constraint.GtConst(new ColumnId(2), new Value(epochs[2])))]));
            Assert.Equal(0, next.Count);
            var kept = next.AddRuleFromCachedPlan(cached, [(new AtomId(0), Constraint.GeConst(new ColumnId(2), new Value(epochs[2])))]);
            Assert.Equal(new RuleId(0), kept);
            using var rs = next.Build();
            var header = Assert.Single(rs[kept!.Value].Instance!.HeaderSubsets);
            Assert.Equal(0b100u, header.LayerMask);
        }

        [Fact]
        public void AViewWithoutLayersClassifiesAndSelectsNothing()
        {
            var store = new Store(ParallelHeuristics.Serial);
            var p = store.AddTable(MaxFunction(1), name: "private", contextPrivate: true);
            var root = store.Root;
            Insert(root, p, root.NewId(), 1, 0);
            using var child = root.CreateChild();
            Assert.Equal(0u, child.GetView(p).PresentMask);
            var fast = new List<Constraint>();
            var slow = new List<Constraint>();
            var subset = child.Database.View.Evaluate(p, [Constraint.GeConst(new ColumnId(2), new Value(0)), Constraint.Eq(new ColumnId(0), new ColumnId(1))], fast, slow);
            Assert.True(subset.IsEmpty);
            Assert.Equal([Constraint.GeConst(new ColumnId(2), new Value(0))], fast);
            Assert.Equal([Constraint.Eq(new ColumnId(0), new ColumnId(1))], slow);
        }

        [Fact]
        public void LayersWhereAConstraintIsSlowRefineByIt()
        {
            // Layer 0 is sorted by column 2, layer 1 not: the range is fast in layer 0 only, so it is fast overall and
            // layer 1's rows are refined by it.
            var levels = new Database[2];
            levels[0] = new Database(ParallelHeuristics.Serial);
            levels[0].AddTable(new SortedWritesTable(1, 3, new ColumnId(2), ReadOnlySpan<ColumnId>.Empty, ClosureMerge.TakeNew, ParallelHeuristics.Serial));
            levels[1] = new Database(ParallelHeuristics.Serial);
            levels[1].AddTable(new SortedWritesTable(1, 3, null, ReadOnlySpan<ColumnId>.Empty, ClosureMerge.TakeNew, ParallelHeuristics.Serial));
            var t = new TableId(0);
            for (uint ts = 0; ts < 4; ts++)
            {
                DatabaseTestUtil.Stage(levels[0], t, [ts, 0, ts]);
                DatabaseTestUtil.Stage(levels[1], t, [10 + ts, 0, ts]);
                levels[0].MergeAll();
                levels[1].MergeAll();
            }

            var layers = new SyntheticLayers(levels);
            layers.Define(t, [true, true], new List<int>?[2], new List<int>?[2]);
            layers.Seal();
            levels[0].AttachView(layers);
            var fast = new List<Constraint>();
            var slow = new List<Constraint>();
            var subset = levels[0].View.Evaluate(t, [Constraint.GeConst(new ColumnId(2), new Value(2))], fast, slow);
            Assert.Single(fast);
            Assert.Empty(slow);
            Assert.Equal([2, 3], ExecutionTestUtil.Ids(subset.GetLayer(0)));
            Assert.Equal([2, 3], ExecutionTestUtil.Ids(subset.GetLayer(1)));
            subset.Dispose(null);
        }

        [Fact]
        public void TheSingleTableFormPutsEveryHeaderInLayerZero()
        {
            var db = new Database(ParallelHeuristics.Serial);
            var t = db.AddTable(new SortedWritesTable(1, 3, new ColumnId(2), ReadOnlySpan<ColumnId>.Empty, ClosureMerge.TakeNew, ParallelHeuristics.Serial));
            for (uint ts = 0; ts < 4; ts++)
            {
                DatabaseTestUtil.Stage(db, t, [ts, 0, ts]);
                db.MergeAll();
            }

            var rsb = db.NewRuleSet();
            var q = rsb.NewRule();
            var k = q.NewVariable("k");
            q.AddAtom(t, [k, QueryEntry.Wildcard(), QueryEntry.Wildcard()], [Constraint.GeConst(new ColumnId(2), new Value(1))]);
            var rb = q.Build();
            rb.AssertEq(k, k);
            var rule = rb.Build("r");
            using var rs = rsb.Build();
            var evaluator = db;
            using var instance = rs[rule].Template.Instantiate(ref evaluator, default, new ActionId(0))!;
            var header = Assert.Single(instance.HeaderSubsets);
            Assert.False(header.IsMultiLayer);
            Assert.Equal(0, header.SingleLayer);
            Assert.Equal([1, 2, 3], ExecutionTestUtil.Ids(header.SingleRows));
            Assert.Equal(ExecutionTestUtil.Ids(rs[rule].Instance!.HeaderSubsets[0].SingleRows), ExecutionTestUtil.Ids(header.SingleRows));
        }

    }

}

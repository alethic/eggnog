using System;
using System.Collections.Generic;
using System.Linq;

using Xunit;

using static Eggnog.Relations.Tests.LayeredTestUtil;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// Tests for <see cref="TableView"/>, <see cref="LayerView"/> and <see cref="ContextTables.GetView"/>
    /// (colored-contexts spec §6.3-§6.5, C4): what a child sees of a table, layer by layer; Lemma F; cleared and private
    /// tables; frozen folds and live unions; caching and invalidation; and <c>LookupPathsAgree</c> on 2000 random
    /// layered states (§17 M9.3 done-when).
    /// </summary>
    /// <remarks>
    /// Environment variable: <c>EGGNOG_LAYERED_SEEDS</c> sets the number of random states (2000 by default).
    /// </remarks>
    public class TableViewTests
    {

        /// <summary>
        /// The random states to check: 2000 by default, or as many as <c>EGGNOG_LAYERED_SEEDS</c> says.
        /// </summary>
        public static TheoryData<int> Seeds()
        {
            var count = int.TryParse(Environment.GetEnvironmentVariable("EGGNOG_LAYERED_SEEDS"), out var n) && n > 0 ? n : 2000;
            var data = new TheoryData<int>();
            for (var seed = 0; seed < count; seed++)
                data.Add(seed);

            return data;
        }

        /// <summary>
        /// §17 M9.3 done-when: on random layered states, the write-phase lookup, the read-phase lookup and a filtered scan
        /// agree after every write, and every context sees exactly its reference.
        /// </summary>
        [Theory]
        [MemberData(nameof(Seeds))]
        public void LookupPathsAgree(int seed)
        {
            var program = new RandomLayeredProgram(seed);
            program.Run();
            Assert.True(program.Batches > 0);
        }

        /// <summary>
        /// The random states reach every layered path: hides, retractions, clears, canonicalized writes, live-set unions
        /// and frozen children.
        /// </summary>
        [Fact]
        public void RandomStatesExerciseEveryLayeredPath()
        {
            long hidden = 0, canonicalized = 0, merges = 0;
            int retracting = 0, clears = 0, frozen = 0;
            for (var seed = 0; seed < 200; seed++)
            {
                var program = new RandomLayeredProgram(seed);
                program.Run();
                hidden += program.RowsHidden;
                canonicalized += program.RowsCanonicalized;
                merges += program.FilterMerges;
                retracting += program.Retracting;
                clears += program.Clears;
                frozen += program.FrozenChildren;
            }

            Assert.True(hidden > 0 && canonicalized > 0 && merges > 0 && retracting > 0 && clears > 0 && frozen > 0, $"hidden {hidden}, canonicalized {canonicalized}, merges {merges}, retracting {retracting}, clears {clears}, frozen {frozen}");
        }

        static (Store Store, TableId F, Context Root) Setup()
        {
            var store = new Store(ParallelHeuristics.Serial);
            var f = store.AddTable(MaxFunction(1), name: "f");
            return (store, f, store.Root);
        }

        [Fact]
        public void AnUntouchedTableSharesItsParentsView()
        {
            var (store, f, root) = Setup();
            var g = store.AddTable(MaxFunction(1), name: "g");
            var a = root.NewId();
            Insert(root, f, a, 1, 0);
            root.Freeze();
            var child = root.CreateChild();
            var grandchild = child.CreateChild();

            // Edge case 7: a context hides nothing of a table it never touched.
            var rootView = root.GetView(f);
            Assert.Same(rootView, store.Schema.RootView(f));
            Assert.Same(rootView, child.GetView(f));
            Assert.Same(rootView, grandchild.GetView(f));
            Assert.Null(child.Tables!.TryGetLayer(f));
            Assert.Equal(1, rootView.LayerCount);
            Assert.True(rootView.Layers[0]!.IsFrozen);
            Assert.Equal(1u, rootView.PresentMask);

            // Writing another table creates no layer of this one.
            Insert(child, g, a, 3, 0);
            Assert.Null(child.Tables.TryGetLayer(f));
            Assert.Same(rootView, child.GetView(f));
            Assert.Equal(new uint[] { a, 1 }, Row(child, f, a)![..2]);
        }

        [Fact]
        public void OwnAndAncestorRowsAreBothVisible()
        {
            var (_, f, root) = Setup();
            var a = root.NewId();
            var b = root.NewId();
            var absent = root.NewId();
            Insert(root, f, a, 1, 0);
            root.Freeze();
            var child = root.CreateChild();
            Insert(child, f, b, 2, 0);

            var view = child.GetView(f);
            Assert.Equal(2, view.LayerCount);
            Assert.Equal(0b11u, view.PresentMask);
            Assert.True(view.Layers[0]!.Hidden.IsNone);
            Assert.True(view.Layers[1]!.Hidden.IsNone);
            Assert.False(view.Layers[1]!.IsFrozen);
            Assert.True(view.FindRow(a, out var layer) >= 0);
            Assert.Equal(0, layer);
            Assert.True(view.FindRow(b, out layer) >= 0);
            Assert.Equal(1, layer);
            Assert.Equal(2, view.VisibleCount);
            Assert.Equal(2, view.ApproximateCount);
            Assert.Equal(-1, view.FindRow(absent, out layer));
            Assert.Equal(-1, layer);
            LookupPaths.Check(child);
        }

        [Fact]
        public void AnOwnRowReplacesTheAncestorRowItHides()
        {
            var (_, f, root) = Setup();
            var a = root.NewId();
            Insert(root, f, a, 1, 0);
            root.Freeze();
            var child = root.CreateChild();

            // A smaller value merges to nothing: the ancestor row stays visible and nothing is hidden.
            Insert(child, f, a, 0, 0);
            Assert.Equal(1u, Row(child, f, a)![1]);
            Assert.True(child.GetView(f).Layers[0]!.Hidden.IsNone);

            // A larger one is stored in the own layer and hides the root's row (KeyHeld).
            Insert(child, f, a, 5, 0);
            var view = child.GetView(f);
            Assert.Equal(5u, Row(child, f, a)![1]);
            Assert.True(view.FindRow(a, out var layer) >= 0);
            Assert.Equal(1, layer);
            var hidden = view.Layers[0]!.Hidden;
            var rootRow = root.Store.Schema[f]!.Root.FindRow(a);
            Assert.Null(hidden.Frozen);
            Assert.NotNull(hidden.Live);
            Assert.True(hidden.Contains(rootRow));
            Assert.Equal(1, view.VisibleCount);
            Assert.Equal(2, view.ApproximateCount);
            Assert.True(view.Layers[0]!.HasInvisible);
            Assert.Equal(1, root.GetView(f).VisibleCount);
#if DEBUG
            Assert.Equal(HideReason.KeyHeld, child.Tables!.TryGetLayer(f)!.ReasonFor(0, rootRow));
#endif
            LookupPaths.Check(child);
        }

        [Fact]
        public void TheFirstLayerHoldingAKeyDecidesEvenWhenItsRowIsHidden()
        {
            // Lemma F: root f(a)=1; C1 raises it to 5 (hiding the root's row) and freezes; C2 deletes it (hiding C1's row).
            // A probe from the top hits C1's hidden row and stops: the root's row, hidden by C1, is never reached.
            var (_, f, root) = Setup();
            var a = root.NewId();
            Insert(root, f, a, 1, 0);
            root.Freeze();
            var c1 = root.CreateChild();
            Insert(c1, f, a, 5, 0);
            c1.Freeze();
            var c2 = c1.CreateChild();
            Remove(c2, f, a);

            var view = c2.GetView(f);
            Assert.Equal(-1, view.FindRow(a, out _));
            Assert.Null(Row(c2, f, a));
            Assert.Equal(0, view.VisibleCount);
            Assert.True(c2.Tables!.TryGetLayer(f)!.HasRetractions);

            // The frozen fold of the root layer at C2 is C1's: one set, nothing live.
            Assert.NotNull(view.Layers[0]!.Hidden.Frozen);
            Assert.Null(view.Layers[0]!.Hidden.Live);
            Assert.Null(view.Layers[1]!.Hidden.Frozen);
            Assert.NotNull(view.Layers[1]!.Hidden.Live);

            // C1 still sees its own row.
            Assert.Equal(5u, Row(c1, f, a)![1]);
            LookupPaths.Check(c1);
            LookupPaths.Check(c2);

            // Writing the key again lands in C2's own layer: the hidden rows below stay hidden.
            Insert(c2, f, a, 2, 0);
            Assert.Equal(2u, Row(c2, f, a)![1]);
            LookupPaths.Check(c2);
        }

        [Fact]
        public void AClearedLevelMakesTheLayersBelowItAbsent()
        {
            var (_, f, root) = Setup();
            var a = root.NewId();
            var b = root.NewId();
            Insert(root, f, a, 1, 0);
            root.Freeze();
            var child = root.CreateChild();
            child.ClearTable(f);
            var view = child.GetView(f);
            Assert.Null(view.Layers[0]);
            Assert.NotNull(view.Layers[1]);
            Assert.Equal(0b10u, view.PresentMask);
            Assert.Null(Row(child, f, a));

            // Rows written afterwards are the child's own; a grandchild sees only them.
            Insert(child, f, b, 4, 0);
            Insert(child, f, a, 0, 0);
            Assert.Equal(0u, Row(child, f, a)![1]);
            child.Freeze();
            var grandchild = child.CreateChild();
            var below = grandchild.GetView(f);
            Assert.Same(child.GetView(f), below);
            Assert.Null(below.Layers[0]);
            Assert.Equal(2, below.VisibleCount);
            LookupPaths.Check(grandchild);
        }

        [Fact]
        public void APrivateTableShowsOnlyTheOwnLayer()
        {
            var store = new Store(ParallelHeuristics.Serial);
            var p = store.AddTable(MaxFunction(1), name: "p", contextPrivate: true);
            var root = store.Root;
            var a = root.NewId();
            Insert(root, p, a, 1, 0);
            var child = root.CreateChild();

            // Edge case 17: a descendant never reads a context-private table; its view has no ancestor layer.
            var view = child.GetView(p);
            Assert.Equal(0u, view.PresentMask);
            Assert.Null(Row(child, p, a));
            Insert(child, p, a, 0, 0);
            Assert.Equal(0u, Row(child, p, a)![1]);
            Assert.Equal(0b10u, child.GetView(p).PresentMask);
            Assert.Null(child.Tables!.TryGetLayer(p)!.Own.CompactionGate);
            Assert.Equal(1u, Row(root, p, a)![1]);
            LookupPaths.Check(child);
        }

        [Fact]
        public void FrozenViewsFoldEveryHideIntoOneSet()
        {
            var (_, f, root) = Setup();
            var ids = Enumerable.Range(0, 6).Select(_ => root.NewId()).ToArray();
            foreach (var id in ids)
                Insert(root, f, id, 1, 0);

            root.Freeze();
            var c1 = root.CreateChild();
            Insert(c1, f, ids[0], 2, 0);
            c1.Freeze();
            var c2 = c1.CreateChild();
            Insert(c2, f, ids[1], 2, 0);
            Remove(c2, f, ids[2]);
            c2.Freeze();

            // C2's frozen view of the root layer holds both levels' hides in its frozen part.
            var frozenView = c2.GetView(f);
            var fold = frozenView.Layers[0]!.Hidden;
            Assert.Null(fold.Live);
            Assert.Equal(3, fold.Frozen!.Count);
            Assert.True(frozenView.Layers[0]!.IsFrozen);
            Assert.True(frozenView.Layers[2]!.IsFrozen);

            // A child of C2 that hides another root row: frozen part from C2's fold, live part its own set.
            var c3 = c2.CreateChild();
            Insert(c3, f, ids[3], 2, 0);
            var view = c3.GetView(f);
            Assert.Same(fold.Frozen, view.Layers[0]!.Hidden.Frozen);
            Assert.Equal(1, view.Layers[0]!.Hidden.Live!.Count);
            Assert.Equal(5, view.VisibleCount);
            Assert.Equal(0, c3.Tables!.Statistics.FilterMerges);
            Assert.Null(Row(c3, f, ids[2]));
            Assert.Equal(2u, Row(c3, f, ids[3])![1]);
            LookupPaths.Check(c3);
        }

        [Fact]
        public void LevelsThatAreNotFrozenUnionTheirLiveSets()
        {
            var (_, f, root) = Setup();
            var ids = Enumerable.Range(0, 4).Select(_ => root.NewId()).ToArray();
            foreach (var id in ids)
                Insert(root, f, id, 1, 0);

            root.Freeze();
            var c1 = root.CreateChild();
            Insert(c1, f, ids[0], 2, 0);
            var c2 = c1.CreateChild();
            Insert(c2, f, ids[1], 2, 0);

            var view = c2.GetView(f);
            var hidden = view.Layers[0]!.Hidden;
            Assert.Null(hidden.Frozen);
            Assert.Equal(2, hidden.Live!.Count);
            Assert.Equal(1, c2.Tables!.Statistics.FilterMerges);
            Assert.False(view.Layers[1]!.IsFrozen);
            Assert.Equal(4, view.VisibleCount);
            Assert.Equal(2u, Row(c2, f, ids[0])![1]);
            Assert.Equal(2u, Row(c2, f, ids[1])![1]);
            LookupPaths.Check(c1);
            LookupPaths.Check(c2);
        }

        [Fact]
        public void ViewsAreCachedUntilTheirStructureChanges()
        {
            var (_, f, root) = Setup();
            var a = root.NewId();
            var b = root.NewId();
            var c = root.NewId();
            Insert(root, f, a, 1, 0);
            root.Freeze();
            var child = root.CreateChild();
            Insert(child, f, b, 1, 0);
            var tables = child.Tables!;
            var generation = tables.ViewGeneration;
            var view = child.GetView(f);
            Assert.Same(view, child.GetView(f));

            // A new own key appends to a present layer: same view, same generation, and the row shows through it.
            Insert(child, f, c, 1, 0);
            Assert.Same(view, child.GetView(f));
            Assert.Equal(generation, tables.ViewGeneration);
            Assert.True(view.FindRow(c, out _) >= 0);

            // A hide changes the structure: the merge epilogue drops the view and moves the generation.
            Insert(child, f, a, 3, 0);
            Assert.NotSame(view, child.GetView(f));
            Assert.True(tables.ViewGeneration > generation);
            Assert.Throws<NotSupportedException>(() => child.GetView(child.Store.UnionFindTable));
            Assert.Throws<NotSupportedException>(() => root.GetView(root.Store.UnionFindTable));
            Assert.Throws<KeyNotFoundException>(() => child.GetView(new TableId(9)));
        }

        [Fact]
        public void WideAndNullaryKeysAreFoundInEveryLayer()
        {
            var store = new Store(ParallelHeuristics.Serial);
            var wide = store.AddTable(MaxFunction(4), name: "wide");
            var nullary = store.AddTable(MaxFunction(0), name: "nullary");
            var root = store.Root;
            var ids = Enumerable.Range(0, 4).Select(_ => root.NewId()).ToArray();
            Insert(root, wide, ids[0], ids[1], ids[2], ids[3], 1, 0);
            Insert(root, nullary, 1, 0);
            root.Freeze();
            var child = root.CreateChild();
            Insert(child, wide, ids[3], ids[2], ids[1], ids[0], 2, 0);
            Insert(child, nullary, 7, 0);

            var view = child.GetView(wide);
            Assert.True(view.FindRow(ids, out var layer) >= 0);
            Assert.Equal(0, layer);
            Assert.True(view.FindRow(ids.Reverse().ToArray(), out layer) >= 0);
            Assert.Equal(1, layer);
            Assert.True(view.TryGetColumn(ids, 0, 4, out var value));
            Assert.Equal(1u, value);
            Assert.Equal(7u, Row(child, nullary)![0]);
            Assert.Equal(1u, Row(root, nullary)![0]);
            Assert.Equal(1, child.GetView(nullary).VisibleCount);
            LookupPaths.Check(child);
        }

    }

}

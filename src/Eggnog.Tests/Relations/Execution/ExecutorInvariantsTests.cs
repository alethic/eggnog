using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

using Xunit;

using static Eggnog.Relations.Tests.LayeredTestUtil;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// The executor part of the context invariant checker (<see cref="ExecutorInvariants"/>) passes on clean contexts
    /// and reports each deliberate corruption of what the executor reads: a view's layers or hidden rows, a frozen
    /// catalog, an own row's sort order, stamp, canonical cells or key, and the semi-naive floor.
    /// </summary>
    public class ExecutorInvariantsTests
    {

        static (Store Store, TableId F, Context Root, Context Child, uint[] Ids) Setup()
        {
            var store = new Store(ParallelHeuristics.Serial);
            var f = store.AddTable(MaxFunction(1), name: "f");
            var root = store.Root;
            var ids = Enumerable.Range(0, 8).Select(_ => root.NewId()).ToArray();
            foreach (var id in ids[..4])
                Insert(root, f, id, 1, 0);

            root.Freeze();
            var child = root.CreateChild();
            foreach (var id in ids[2..6])
                Insert(child, f, id, 5, 0);

            return (store, f, root, child, ids);
        }

        static List<string> Check(Context context)
        {
            var violations = new List<string>();
            ExecutorInvariants.Check(context, violations);
            return violations;
        }

        static void SetCachedView(Context child, TableId table, TableView view)
        {
            var field = typeof(ContextTables).GetField("views", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var views = (TableView?[])field.GetValue(child.Tables)!;
            views[table.Index] = view;
        }

        [Fact]
        public void CleanContextsPass()
        {
            var (_, _, root, child, _) = Setup();
            Assert.Empty(Check(root));
            Assert.Empty(Check(child));
            child.CheckInvariants();
            child.Freeze();
            Assert.Empty(Check(child));
            using var grandchild = child.CreateChild();
            Assert.Empty(Check(grandchild));
        }

        [Fact]
        public void AnOwnRowOutOfOrderOrBelowCreationIsReported()
        {
            var (_, f, _, child, _) = Setup();
            var own = child.Tables!.TryGetLayer(f)!.Own;
            var arity = own.Arity;
            own.Rows.Data[2 * arity + 2] = 0;
            var violations = Check(child);
            Assert.Contains(violations, v => v.StartsWith("sort order") && v.Contains("below the previous row"));
            Assert.Contains(violations, v => v.StartsWith("sort order") && v.Contains("run index"));
            Assert.Contains(violations, v => v.StartsWith("creation stamps"));
        }

        [Fact]
        public void AStaleIdInAnOwnRowIsReported()
        {
            var (_, f, _, child, ids) = Setup();
            child.Union(ids[6], ids[7]);
            child.Rebuild();
            var own = child.Tables!.TryGetLayer(f)!.Own;
            own.Rows.Data[0] = ids[7];
            Assert.Contains(Check(child), v => v.StartsWith("canonical view") && v.Contains($"names {ids[7]}"));
        }

        [Fact]
        public void AViewThatLosesALayerOrHidesTheWrongRowsIsReported()
        {
            var (_, f, _, child, _) = Setup();
            var view = child.GetView(f);

            // The root's layer dropped: the view does not show what the chain holds.
            SetCachedView(child, f, new TableView(f, view.Spec, [null, view.Layers[1]]));
            Assert.Contains(Check(child), v => v.StartsWith("view coherence") && v.Contains("depth 0") && v.Contains("absent, expected present"));

            // The child's own row hidden from its own view: its key's lookup fails, and no level hides that row.
            var own = view.Layers[1]!;
            var set = new HiddenSet(own.Table.Version.Major);
            set.Add(0, ((SortedWritesTable)own.Table).Rows.Count);
            set.Seal(((SortedWritesTable)own.Table).Rows.Count);
            SetCachedView(child, f, new TableView(f, view.Spec, [view.Layers[0], new LayerView(1, own.Info, new HiddenRows(null, set), false)]));
            var violations = Check(child);
            Assert.Contains(violations, v => v.StartsWith("unique keys"));
            Assert.Contains(violations, v => v.StartsWith("view coherence") && v.Contains("which no level hides"));

            // A row the child hides shown again.
            SetCachedView(child, f, new TableView(f, view.Spec, [new LayerView(0, view.Layers[0]!.Info, HiddenRows.None, true), view.Layers[1]]));
            Assert.Contains(Check(child), v => v.StartsWith("view coherence") && v.Contains("which the view shows"));
        }

        [Fact]
        public void HiddenRowsOutOfRangeOrOfAnotherGenerationAreReported()
        {
            var (_, f, _, child, _) = Setup();
            var view = child.GetView(f);
            var root = view.Layers[0]!;
            var count = ((SortedWritesTable)root.Table).Rows.Count;
            var wide = new HiddenSet(root.Table.Version.Major);
            wide.Add(count + 3, count + 10);
            wide.Seal(count + 10);
            SetCachedView(child, f, new TableView(f, view.Spec, [new LayerView(0, root.Info, new HiddenRows(wide, root.Hidden.Live), true), view.Layers[1]]));
            Assert.Contains(Check(child), v => v.StartsWith("hidden rows") && v.Contains($"which has {count} rows"));

            var stale = new HiddenSet(root.Table.Version.Major + 1);
            stale.Add(2, count);
            stale.Seal(count);
            SetCachedView(child, f, new TableView(f, view.Spec, [new LayerView(0, root.Info, new HiddenRows(stale, null), true), view.Layers[1]]));
            Assert.Contains(Check(child), v => v.StartsWith("hidden rows") && v.Contains("generation"));
        }

        [Fact]
        public void AFrozenCatalogBehindItsTableIsReported()
        {
            var (store, f, root, child, _) = Setup();
            var info = store.Schema[f]!.RootInfo;
            info.GetColumnIndex(new ColumnId(1));
            Assert.Empty(Check(root));
            var entry = info.ColumnIndexes.Entries.ToArray().Single(e => e.Key.SequenceEqual([new ColumnId(1)]));
            Assert.True(entry.Index.TryGet(out var index));
            typeof(Index<ColumnIndex>).GetField("updatedTo", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(index, default(TableVersion));
            Assert.Contains(Check(root), v => v.StartsWith("frozen catalogs"));
            Assert.Contains(Check(child), v => v.StartsWith("frozen catalogs"));
        }

        [Fact]
        public void AFloorAboveTheClockIsReported()
        {
            var (store, _, _, child, _) = Setup();
            child.NoteRuleRun(store.ReadClock() + 100);
            Assert.Contains(Check(child), v => v.StartsWith("watermark bounds"));
            Assert.Throws<InvalidOperationException>(() => child.CheckInvariants());
        }

    }

}

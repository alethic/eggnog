using System;
using System.Collections.Generic;
using System.Linq;

using Xunit;

using static Eggnog.Relations.Tests.IndexTestUtil;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// Review (M10.1, soundness): every visibility-aware path of §9.3 (hidden-aware scans, projections, column walks,
    /// refinements, per-subset index builds, <see cref="RowOps.IsVisible"/>, <see cref="TableView.AnyVisible"/> and
    /// <see cref="TableView.AllRows"/>) agrees, on the hidden rows of real context views (frozen folds, live unions,
    /// clears, retractions, catch-up hides and step-4 hides), with the definition of visibility in §6.4, computed
    /// directly from each level's layer and hidden sets (<see cref="TableLayer.IsHiddenHere"/>), not from the view.
    /// </summary>
    public class HiddenAwareViewAgreementReviewTests
    {

        /// <summary>
        /// The random states to check: 400 by default, or as many as <c>EGGNOG_REVIEW_SEEDS</c> says (4000 were run in
        /// review).
        /// </summary>
        public static TheoryData<int> Seeds()
        {
            var data = new TheoryData<int>();
            var count = int.TryParse(Environment.GetEnvironmentVariable("EGGNOG_REVIEW_SEEDS"), out var n) && n > 0 ? n : 400;
            for (var seed = 0; seed < count; seed++)
                data.Add(seed);

            return data;
        }

        /// <summary>
        /// Builds a column id.
        /// </summary>
        static ColumnId C(int column) => new(column);

        /// <summary>
        /// Every visibility-aware path agrees with §6.4 on every layer of every context's view, after every step of a
        /// random program of writes, retractions, clears, unions, freezes and child creations.
        /// </summary>
        [Theory]
        [MemberData(nameof(Seeds))]
        public void HiddenAwarePathsAgreeWithTheVisibilityDefinition(int seed) => Run(seed);

        /// <summary>
        /// The random states reach hidden rows, frozen folds, live parts and unioned live parts, so the agreement above is
        /// not vacuous.
        /// </summary>
        [Fact]
        public void TheRandomStatesHideRowsInEveryWay()
        {
            long hidden = 0, frozenParts = 0, liveParts = 0, unions = 0;
            for (var seed = 0; seed < 100; seed++)
            {
                var stats = Run(seed);
                hidden += stats.Hidden;
                frozenParts += stats.FrozenParts;
                liveParts += stats.LiveParts;
                unions += stats.Unions;
            }

            Assert.True(hidden > 0 && frozenParts > 0 && liveParts > 0 && unions > 0, $"hidden {hidden}, frozen parts {frozenParts}, live parts {liveParts}, unioned live parts {unions}");
        }

        /// <summary>
        /// Runs one random program, checking after every step, and returns what its views hid.
        /// </summary>
        static (long Hidden, long FrozenParts, long LiveParts, long Unions) Run(int seed)
        {
            long hiddenRows = 0, frozenParts = 0, liveParts = 0, unions = 0;
            var random = new Random(seed);
            var store = new Store(ParallelHeuristics.Serial);
            var tables = new List<TableId>
            {
                store.AddTable(LayeredTestUtil.MaxFunction(1), name: "f1"),
                store.AddTable(LayeredTestUtil.MaxFunction(2), name: "f2"),
                store.AddTable(LayeredTestUtil.Relation(1), name: "r1"),
                store.AddTable(LayeredTestUtil.ValueOnly(), name: "vals"),
                store.AddTable(LayeredTestUtil.MaxFunction(1), name: "priv", contextPrivate: true),
            };

            var ids = Enumerable.Range(0, 12).Select(_ => store.Root.NewId()).ToArray();
            var contexts = new List<Context> { store.Root };
            var checks = 0;

            for (var step = 0; step < 30; step++)
            {
                var live = contexts.Where(c => c.IsDisposed == false).ToList();
                var context = live[random.Next(live.Count)];
                var action = random.Next(10);
                if (action < 5 && context.IsFrozen == false)
                {
                    Write(random, store, context, tables, ids);
                }
                else if (action == 5 && context.IsFrozen == false)
                {
                    var a = context.Find(ids[random.Next(ids.Length)]);
                    var b = context.Find(ids[random.Next(ids.Length)]);
                    context.Union(a, b);
                    context.Rebuild();
                }
                else if (action == 6 && context.IsFrozen == false && MayRetract(context))
                {
                    context.ClearTable(tables[random.Next(tables.Count)]);
                }
                else if (action == 7 && context.IsFrozen == false && (context.IsRoot || context.Parent!.IsFrozen))
                {
                    RebuildAll(contexts);
                    context.Freeze();
                }
                else if (action >= 8 && context.Depth < 5 && contexts.Count < 8)
                {
                    RebuildAll(contexts);
                    contexts.Add(context.CreateChild());
                }
                else
                {
                    continue;
                }

                RebuildAll(contexts);
                foreach (var c in contexts.Where(c => c.IsDisposed == false))
                {
                    checks += Check(random, store, c, tables, $"seed {seed} step {step} depth {c.Depth}");
                    foreach (var t in tables)
                    {
                        var v = c.GetView(t);
                        foreach (var l in v.Layers)
                        {
                            if (l is null)
                                continue;

                            frozenParts += l.Hidden.Frozen is null ? 0 : 1;
                            liveParts += l.Hidden.Live is null ? 0 : 1;
                            hiddenRows += Enumerable.Range(0, l.Sorted!.Rows.Count).Count(l.Hidden.Contains);
                        }
                    }

                    unions += c.Tables?.Statistics.FilterMerges ?? 0;
                }
            }

            for (var i = contexts.Count - 1; i > 0; i--)
                contexts[i].Dispose();

            Assert.True(checks > 0);
            return (hiddenRows, frozenParts, liveParts, unions);
        }

        /// <summary>
        /// Whether <paramref name="context"/> may retract shared rows (§6.7): no live descendants, and a frozen parent in a
        /// child.
        /// </summary>
        static bool MayRetract(Context context) => context.Gate.HasLiveDescendants == false && (context.IsRoot || context.Parent!.IsFrozen);

        /// <summary>
        /// Brings every writable context up to date, parents first.
        /// </summary>
        static void RebuildAll(List<Context> contexts)
        {
            foreach (var c in contexts)
                if (c.IsDisposed == false && c.IsFrozen == false)
                    c.Rebuild();
        }

        /// <summary>
        /// One write batch: inserts with canonical ids and random values, and removals where allowed.
        /// </summary>
        static void Write(Random random, Store store, Context context, List<TableId> tables, uint[] ids)
        {
            var retract = MayRetract(context);
            var ops = new List<(TableId Table, uint[] Row, bool Remove)>();
            for (var n = random.Next(1, 6); n > 0; n--)
            {
                var table = tables[random.Next(tables.Count)];
                var schema = store.Schema[table]!;
                var key = new uint[schema.KeyCount];
                for (var c = 0; c < key.Length; c++)
                    key[c] = schema.RebuildColumns.Length > 0 ? context.Find(ids[random.Next(ids.Length)]) : (uint)random.Next(6);

                if ((retract || schema.IsContextPrivate) && random.Next(4) == 0)
                {
                    ops.Add((table, key, true));
                    continue;
                }

                var row = new uint[schema.Arity];
                key.CopyTo(row, 0);
                if (schema.Root.MergeFunction is MaxMerge max)
                    row[max.ValueColumn] = (uint)random.Next(20);

                ops.Add((table, row, false));
            }

            context.Write(state =>
            {
                foreach (var (table, row, remove) in ops)
                {
                    if (remove)
                    {
                        state.StageRemove(table, row);
                        continue;
                    }

                    var copy = (uint[])row.Clone();
                    if (store.Schema[table]!.SortColumn is var sort and >= 0)
                        copy[sort] = state.Timestamp;

                    state.StageInsert(table, copy);
                }

                return 0;
            });
        }

        /// <summary>
        /// The table of layer <paramref name="j"/> of <paramref name="context"/>'s chain for <paramref name="table"/>, or
        /// <c>null</c>.
        /// </summary>
        static SortedWritesTable? LayerTable(Store store, Context context, TableId table, int j) =>
            j == 0 ? store.Schema[table]!.Root : context.Ancestors[j].Tables!.TryGetLayer(table)?.Own;

        /// <summary>
        /// §6.4: the layer is in the view (present, not below a level that cleared the table; only the own layer of a
        /// private table).
        /// </summary>
        static bool PresentByDefinition(Store store, Context context, TableId table, int j)
        {
            if (LayerTable(store, context, table, j) is null)
                return false;

            if (store.Schema[table]!.IsContextPrivate && j != context.Depth)
                return false;

            for (var i = j + 1; i <= context.Depth; i++)
                if (context.Ancestors[i].Tables!.TryGetLayer(table) is { IsCleared: true })
                    return false;

            return true;
        }

        /// <summary>
        /// §6.4: <c>visible_V(j, r) ⇔ live_j(r) ∧ ∀ i ∈ (j, d_V]: ¬cleared_i(t) ∧ r ∉ H_i[t][j]</c>, read from each level's
        /// layer (any state), not from the view.
        /// </summary>
        static HashSet<int> VisibleByDefinition(Store store, Context context, TableId table, int j)
        {
            var result = new HashSet<int>();
            if (PresentByDefinition(store, context, table, j) == false)
                return result;

            var layer = LayerTable(store, context, table, j)!;
            for (var r = 0; r < layer.Rows.Count; r++)
            {
                if (layer.Rows.Data[r * layer.Arity] == Value.StaleRep)
                    continue;

                var hidden = false;
                for (var i = j + 1; i <= context.Depth && hidden == false; i++)
                    hidden = context.Ancestors[i].Tables!.TryGetLayer(table) is { } l && l.IsHiddenHere(j, r);

                if (hidden == false)
                    result.Add(r);
            }

            return result;
        }

        /// <summary>
        /// Checks every table of <paramref name="context"/>; returns the number of layers checked.
        /// </summary>
        static int Check(Random random, Store store, Context context, List<TableId> tables, string where)
        {
            var checkedLayers = 0;
            var pool = new SubsetArrayPool(context.Depth + 1);
            foreach (var table in tables)
            {
                var view = context.GetView(table);
                var name = $"{where} table {store.Schema.GetName(table)}";

                // Layers the view has, and the chain levels beyond them (a view shared with an ancestor).
                for (var j = 0; j <= context.Depth; j++)
                {
                    var expected = VisibleByDefinition(store, context, table, j);
                    var lv = j < view.LayerCount ? view.Layers[j] : null;
                    if (lv is null)
                    {
                        Assert.True(expected.Count == 0, $"{name} layer {j}: the view has no layer, but {expected.Count} rows are visible by definition");
                        Assert.False(view.AnyVisible(j, SubsetRef.Dense(new OffsetRange(0, 1))), $"{name} layer {j}: AnyVisible on an absent layer");
                        continue;
                    }

                    Assert.True(PresentByDefinition(store, context, table, j), $"{name} layer {j}: present in the view but not by definition");
                    CheckLayer(random, view, j, lv, expected, pool, $"{name} layer {j}");
                    checkedLayers++;
                }

                // AllRows: every present layer with rows, densely.
                var all = view.AllRows(pool);
                for (var j = 0; j < LayeredSubset.MaxLayers; j++)
                {
                    var rows = j < view.LayerCount && view.Layers[j] is { } l ? l.Sorted!.Rows.Count : 0;
                    var got = OffsetTestUtil.Collect(all.GetLayer(j));
                    Assert.True(Enumerable.Range(0, rows).SequenceEqual(got), $"{name}: AllRows layer {j} holds {got.Count} rows, expected {rows}");
                }

                all.Dispose(pool);
            }

            return checkedLayers;
        }

        /// <summary>
        /// Checks one layer of a view against the rows visible by definition.
        /// </summary>
        static void CheckLayer(Random random, TableView view, int j, LayerView lv, HashSet<int> expected, SubsetArrayPool pool, string name)
        {
            var table = lv.Sorted!;
            var count = table.Rows.Count;
            var arity = table.Arity;
            uint[] Cells(int r) => table.Rows.Data.AsSpan(r * arity, arity).ToArray();

            // The view's own membership test agrees with the definition, row by row (so does RowOps).
            for (var r = 0; r < count; r++)
            {
                var visible = table.Rows.Data[r * arity] != Value.StaleRep && lv.Hidden.Contains(r) == false;
                Assert.True(expected.Contains(r) == visible, $"{name}: row {r} is {(visible ? "visible" : "invisible")} in the view, {(expected.Contains(r) ? "visible" : "invisible")} by definition");
                Assert.Equal(visible, RowOps.IsVisible(table.Rows.Data, r * arity, r, lv.Hidden.Frozen, lv.Hidden.Live));
            }

            for (var trial = 0; trial < 3; trial++)
            {
                var subset = trial == 0 || count == 0
                    ? Subset.Dense(0, count)
                    : random.Next(2) == 0
                        ? OffsetTestUtil.Sparse(OffsetTestUtil.RandomSorted(random, random.Next(1, count + 1), count, duplicates: false))
                        : Subset.Dense(random.Next(count), count);

                var subsetRows = OffsetTestUtil.Collect(subset);
                var inSubset = subsetRows.Where(expected.Contains).ToList();

                // ScanGeneric (hidden-aware), through the base class.
                var sink = new RowCollector();
                lv.Table.ScanGeneric(subset.AsRef(), lv.Hidden, ref sink);
                Assert.True(inSubset.SequenceEqual(sink.Rows.Select(x => x.Id)), $"{name}: ScanGeneric gives [{string.Join(",", sink.Rows.Select(x => x.Id))}], expected [{string.Join(",", inSubset)}]");

                // Chunked, constrained scans: the value or timestamp column against a constant.
                var column = random.Next(arity);
                var constant = new Value((uint)random.Next(25));
                var constraint = random.Next(3) switch
                {
                    0 => Constraint.EqConst(C(column), constant),
                    1 => Constraint.LtConst(C(column), constant),
                    _ => Constraint.GeConst(C(column), constant),
                };

                Constraint[] constraints = [constraint];
                var filtered = inSubset.Where(r => constraint.Evaluate(Cells(r))).ToList();
                foreach (var chunk in new[] { 1, 7, 64, int.MaxValue })
                {
                    var chunked = new RowCollector();
                    for (var position = 0; position >= 0;)
                        position = lv.Table.ScanGenericBounded(subset.AsRef(), position, chunk, constraints, lv.Hidden, ref chunked);

                    Assert.True(filtered.SequenceEqual(chunked.Rows.Select(x => x.Id)), $"{name}: chunk {chunk} scan of {constraint} gives [{string.Join(",", chunked.Rows.Select(x => x.Id))}], expected [{string.Join(",", filtered)}]");

                    var projected = new TaggedRowBuffer(1);
                    for (var position = 0; position >= 0;)
                        position = lv.Table.ScanProject(subset.AsRef(), [C(column)], position, chunk, constraints, lv.Hidden, projected);

                    Assert.True(filtered.SequenceEqual(TableTestUtil.DumpBuffer(projected).Select(x => x.Id)), $"{name}: chunk {chunk} projection");
                }

                // The column walk.
                var walk = new RowCollector();
                lv.Table.ForEachColumn(subset.AsRef(), C(column), lv.Hidden, ref walk);
                Assert.True(inSubset.SequenceEqual(walk.Rows.Select(x => x.Id)), $"{name}: ForEachColumn");

                // Refinements: the layer view's and the table's.
                var refined = lv.RefineVisible(subset.Clone());
                Assert.True(inSubset.SequenceEqual(OffsetTestUtil.Collect(refined)), $"{name}: LayerView.RefineVisible gives [{string.Join(",", OffsetTestUtil.Collect(refined))}], expected [{string.Join(",", inSubset)}]");
                refined.Dispose();
                var refinedTable = lv.Table.RefineVisible(subset.Clone(), lv.Hidden);
                Assert.True(inSubset.SequenceEqual(OffsetTestUtil.Collect(refinedTable)), $"{name}: Table.RefineVisible");
                refinedTable.Dispose();

                // Existence.
                Assert.True(inSubset.Count > 0 == view.AnyVisible(j, subset.AsRef()), $"{name}: AnyVisible over [{string.Join(",", subsetRows)}] is {view.AnyVisible(j, subset.AsRef())}, expected {inSubset.Count > 0}");

                // Per-subset index builds: column index (both build paths), tuple index, sparse index.
                var expectedByColumn = inSubset.GroupBy(r => Cells(r)[column]).ToDictionary(g => g.Key, g => g.ToArray());
                var index = ColumnIndex.BuildForSubset(lv.Table, subset.AsRef(), C(column), lv.Hidden);
                AssertExactly(expectedByColumn, Entries(index), $"{name}: ColumnIndex.BuildForSubset");
                index.Dispose();
                var full = new ColumnIndex(ParallelHeuristics.Serial);
                full.RebuildFull(lv.Table, [C(column)], subset.AsRef(), lv.Hidden);
                AssertExactly(expectedByColumn, Entries(full), $"{name}: ColumnIndex.RebuildFull");
                full.Dispose();

                var columns = new[] { 0, column }.Distinct().ToArray();
                var expectedByTuple = inSubset.GroupBy(r => KeyOf(columns.Select(c => Cells(r)[c]).ToArray())).ToDictionary(g => g.Key, g => g.ToArray());
                var tuple = TupleIndex.BuildForSubset(lv.Table, subset.AsRef(), Ids(columns), lv.Hidden);
                AssertExactly(expectedByTuple, Entries(tuple), $"{name}: TupleIndex.BuildForSubset");
                tuple.Dispose();

                if (subsetRows.Count <= SparseColumnIndex.Capacity)
                {
                    var sparse = new SparseColumnIndex();
                    sparse.Build(lv.Table, subset.AsRef(), C(column), lv.Hidden);
                    var got = new Dictionary<uint, int[]>();
                    for (var e = 0; e < sparse.Count; e++)
                        got[sparse.KeyAt(e)] = ToArray(sparse.RowsAt(e));

                    AssertExactly(expectedByColumn, got, $"{name}: SparseColumnIndex.Build");
                }

                subset.Dispose();
            }
        }

    }

}

using System;
using System.Linq;

using Xunit;

using static Eggnog.Relations.Tests.ActionTestUtil;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// Tests for the action wiring in a child: the lookup instructions read the context's view through the
    /// <c>LayeredKey*Lookup</c> probes (every key width), <c>LookupOrInsertDefault</c> creates its buffer only at a
    /// miss, predictions use the write-phase lookup, the union-find is read through the child's union-find table,
    /// committed tables are never read as single tables, and <see cref="ExecutionState.VisibleRowCount"/> counts what
    /// the context sees, for the union-find as for any table.
    /// </summary>
    public class LayeredLookupTests
    {

        /// <summary>
        /// A store with functions of 1 to 4 keys; the root holds f(ids[0..]) = 1 for each, then freezes; a child raises
        /// ids[1] to 5 (hiding the root row) and adds ids[3] = 7.
        /// </summary>
        static (Store Store, TableId[] F, Context Child, uint[] Ids) Setup()
        {
            var store = new Store(ParallelHeuristics.Serial);
            var f = Enumerable.Range(1, 4).Select(keys => store.AddTable(LayeredTestUtil.MaxFunction(keys), name: $"f{keys}")).ToArray();
            var root = store.Root;
            var ids = Enumerable.Range(0, 6).Select(_ => root.NewId()).ToArray();
            for (var k = 0; k < 4; k++)
                for (var i = 0; i < 3; i++)
                    LayeredTestUtil.Insert(root, f[k], Key(ids[i], k + 1).Append(1u).Append(0u).ToArray());

            root.Union(ids[4], ids[5]);
            root.Rebuild();
            root.Freeze();
            var child = root.CreateChild();
            for (var k = 0; k < 4; k++)
            {
                LayeredTestUtil.Insert(child, f[k], Key(ids[1], k + 1).Append(5u).Append(0u).ToArray());
                LayeredTestUtil.Insert(child, f[k], Key(ids[3], k + 1).Append(7u).Append(0u).ToArray());
            }

            return (store, f, child, ids);
        }

        /// <summary>
        /// A key of <paramref name="width"/> cells, all <paramref name="id"/>.
        /// </summary>
        static uint[] Key(uint id, int width) => Enumerable.Repeat(id, width).ToArray();

        /// <summary>
        /// The key operands: variable 0 repeated.
        /// </summary>
        static QueryEntry[] Args(int width) => Enumerable.Repeat(QueryEntry.Var(V(0)), width).ToArray();

        [Fact]
        public void LookupsReadTheViewAtEveryKeyWidth()
        {
            var (_, f, child, ids) = Setup();
            var probe = new[] { ids[0], ids[1], ids[2], ids[3], ids[4] };
            for (var k = 0; k < 4; k++)
            {
                var state = child.Database.CreateExecutionState();
                Assert.True(state.InChildContext);

                // Lookup drops the lanes whose key is missing; the hidden root row of ids[1] is never returned.
                var b = Batch((0, probe));
                var live = state.RunInstrs([new LookupInstr(f[k], Args(k + 1), C(k + 1), V(1))], b);
                Assert.Equal(4, live);
                Assert.Equal(new uint[] { 1, 5, 1, 7, S }, Column(b, 1));

                // LookupWithDefault fills the missing lane.
                b = Batch((0, probe));
                Assert.Equal(5, state.RunInstrs([new LookupWithDefaultInstr(f[k], Args(k + 1), C(k + 1), V(1), K(42))], b));
                Assert.Equal(new uint[] { 1, 5, 1, 7, 42 }, Column(b, 1));

                // The same through a clone, as action batches run.
                var clone = state.Clone();
                Assert.True(clone.InChildContext);
                b = Batch((0, probe));
                Assert.Equal(4, clone.RunInstrs([new LookupInstr(f[k], Args(k + 1), C(k + 1), V(1))], b));
                Assert.Equal(new uint[] { 1, 5, 1, 7, S }, Column(b, 1));
            }
        }

        [Fact]
        public void ALookupThatHitsCreatesNoLayer()
        {
            var store = new Store(ParallelHeuristics.Serial);
            var g = store.AddTable(LayeredTestUtil.Constructor(store), [], [store.UnionFindTable], "g");
            var root = store.Root;
            var a = root.NewId();
            var v = root.NewId();
            var b = root.NewId();
            LayeredTestUtil.Insert(root, g, a, v, 0);
            root.Freeze();
            var child = root.CreateChild();

            // A hit: the root's value, no buffer, no layer.
            var state = child.Database.CreateExecutionState();
            var batch = Batch((0, [a]));
            state.RunInstrs([new LookupOrInsertDefaultInstr(g, [V(0)], [WriteVal.FromCounter(store.IdCounter), WriteVal.Of(K(0))], C(1), V(1))], batch);
            Assert.Equal(new[] { v }, Column(batch, 1));
            Assert.Null(child.Tables!.TryGetLayer(g));
            Assert.Equal(0, state.PredictedCount);

            // A miss: a fresh id local to the child, staged into the child's new layer.
            batch = Batch((0, [b]));
            state.RunInstrs([new LookupOrInsertDefaultInstr(g, [V(0)], [WriteVal.FromCounter(store.IdCounter), WriteVal.Of(K(0))], C(1), V(1))], batch);
            var fresh = Column(batch, 1)[0];
            Assert.True(child.IsLocalId(fresh));
            Assert.NotNull(child.Tables.TryGetLayer(g));
            state.Flush();
            child.Database.MergeAll(store.ReadClock());
            Assert.Equal(fresh, LayeredTestUtil.Row(child, g, b)![1]);

            // A row of the wrong width is refused against the store's schema, before anything is staged.
            var wrong = child.Database.CreateExecutionState();
            Assert.Throws<InvalidOperationException>(() => wrong.RunInstrs([new LookupOrInsertDefaultInstr(g, [V(0)], [WriteVal.Of(K(0))], C(1), V(1))], Batch((0, [999_999u]))));
        }

        [Fact]
        public void PredictionsUseTheWriteLookup()
        {
            var (store, f, child, ids) = Setup();
            var state = child.Database.CreateExecutionState();
            MergeVal[] defaults = [MergeVal.FromConstant(new Value(99)), MergeVal.FromConstant(new Value(0))];

            Assert.Equal(new uint[] { ids[1], 5, 0 }.Take(2), state.PredictRow(f[0], [ids[1]], defaults).Take(2));
            Assert.Equal(1u, state.PredictColumn(f[0], [ids[0]], defaults, C(1)));
            Assert.Equal(7u, state.PredictColumn(f[0], new[] { ids[3] }, 0, 1, defaults, C(1)));
            var row = new uint[3];
            state.PredictRow(f[0], [ids[2]], defaults, row);
            Assert.Equal(1u, row[1]);

            // A miss predicts and stages into the child.
            Assert.Equal(99u, state.PredictColumn(f[0], [ids[4]], defaults, C(1)));
            Assert.Equal(1, state.PredictedCount);
            Assert.Throws<ArgumentException>(() => state.PredictRow(f[0], [ids[1]], defaults, new uint[2]));
            Assert.Throws<ArgumentException>(() => state.PredictColumn(f[1], [ids[5]], defaults, C(1)));
            Assert.Equal(4, state.VisibleRowCount(f[0]));

            // The union-find counts the ids displaced in the child's view: the root's one, as a copy of the context
            // would.
            Assert.Equal(1, state.VisibleRowCount(store.UnionFindTable));
        }

        [Fact]
        public void TheUnionFindIsReadThroughTheChildsUnionFind()
        {
            var (store, _, child, ids) = Setup();
            var uf = store.UnionFindTable;
            var state = child.Database.CreateExecutionState();
            var b = Batch((0, [ids[4], ids[5], ids[0]]));

            // Column 1 is the canonical id in the child, for every id.
            Assert.Equal(3, state.RunInstrs([new LookupInstr(uf, [V(0)], C(1), V(1))], b));
            Assert.Equal(new[] { ids[4], ids[4], ids[0] }, Column(b, 1));
            var row = new uint[3];
            Assert.True(child.TryGetRow(uf, [ids[5]], row));
            Assert.Equal(ids[4], row[1]);
            Assert.Equal(ids[4], state.PredictColumn(uf, [ids[5]], [MergeVal.FromConstant(new Value(0)), MergeVal.FromConstant(new Value(0))], C(1)));
        }

        [Fact]
        public void CommittedTablesAreNeverReadAsSingleTables()
        {
            var (store, f, child, _) = Setup();
            var state = child.Database.CreateExecutionState();
            Assert.Throws<InvalidOperationException>(() => state.GetTable(f[0]));
            Assert.Throws<InvalidOperationException>(() => state.Clone().GetTable(f[0]));

            // The root's states read single tables.
            var rootState = store.Root.Database.CreateExecutionState();
            Assert.False(rootState.InChildContext);
            Assert.Same(store.Schema[f[0]]!.Root, rootState.GetTable(f[0]));
            Assert.Equal(3, rootState.VisibleRowCount(f[0]));
        }

        /// <summary>
        /// A child that writes nothing counts the union-find rows its parent counts, as a copy of the context would.
        /// </summary>
        [Fact]
        public void AnUntouchedChildCountsTheUnionFindRowsItSees()
        {
            var store = new Store(ParallelHeuristics.Serial);
            var root = store.Root;
            var a = root.NewId();
            var b = root.NewId();
            root.Union(a, b);
            root.Rebuild();
            root.Freeze();
            var child = root.CreateChild();
            var uf = store.UnionFindTable;

            // The child sees the root's displacement row...
            var row = new uint[3];
            Assert.True(child.TryGetRow(uf, [b], row));
            Assert.Equal(a, row[1]);

            // ...so it counts it, as the root does.
            var rootCount = root.Database.CreateExecutionState().VisibleRowCount(uf);
            Assert.Equal(1, rootCount);
            Assert.Equal(rootCount, child.Database.CreateExecutionState().VisibleRowCount(uf));
        }

        /// <summary>
        /// <see cref="ExecutionState.VisibleRowCount"/> of the union-find in a child counts the ids that are not
        /// canonical in the child's view, the displacements its ancestors made before it was created included, as a
        /// copy of the context would; it is not the count of the child's own journal rows. The union-find has no view
        /// in a child, so a refusal (<see cref="NotSupportedException"/>) passes too. A child that unions nothing
        /// reports what its root, whose rows it sees, reports: 1.
        /// </summary>
        [Fact]
        public void AChildsUnionFindRowCountIsNotItsJournalCount()
        {
            var store = new Store(ParallelHeuristics.Serial);
            var root = store.Root;
            var a = root.NewId();
            var b = root.NewId();
            root.Union(a, b);
            root.Rebuild();
            root.Freeze();
            var child = root.CreateChild();
            Assert.Equal(a, child.Find(b));

            var rootCount = root.Database.CreateExecutionState().VisibleRowCount(store.UnionFindTable);
            Assert.Equal(1, rootCount);

            long childCount;
            try
            {
                childCount = child.Database.CreateExecutionState().VisibleRowCount(store.UnionFindTable);
            }
            catch (NotSupportedException)
            {
                // The union-find has no view in a child.
                return;
            }

            Assert.Equal(rootCount, childCount);
        }

    }

}

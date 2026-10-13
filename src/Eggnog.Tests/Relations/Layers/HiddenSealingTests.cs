using System;
using System.Linq;

using Xunit;

using static Eggnog.Relations.Tests.LayeredTestUtil;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// Tests for the sealing discipline (colored-contexts spec X40, I18; review finding R1-1): every merge of a child
    /// seals the hidden sets it touched before it returns, so the read paths, which probe sealed sets only, see every hide
    /// at once, also after a merge that grows no union-find.
    /// </summary>
    /// <remarks>
    /// R1-1's rule half ("a rule over <c>f</c> derives <c>g(a,5)</c> only") runs a rule in the child through the layered
    /// executor (M10.2, <see cref="ARuleOverTheViewDerivesOnlyTheChildsValue"/>); the scan half reads the view directly.
    /// </remarks>
    public class HiddenSealingTests
    {

        static (Store Store, TableId F, Context Child, uint A) Setup()
        {
            var store = new Store(ParallelHeuristics.Serial);
            var f = store.AddTable(MaxFunction(1), name: "f");
            var root = store.Root;
            var a = root.NewId();
            Insert(root, f, a, 1, 0);
            root.Freeze();
            return (store, f, root.CreateChild(), a);
        }

        [Fact]
        public void AScanOfTheViewSeesOnlyTheChildsValue()
        {
            // R1-1: f(Id) -> i64 by max with a timestamp column; root f(a)=1, frozen; the child writes f(a)=5, a merge that
            // grows no union-find. A scan of the child's view yields f(a)=5 only.
            var (_, f, child, a) = Setup();
            Insert(child, f, a, 5, 0);
            Assert.Equal(0, child.Tables!.UnsealedCount);

            var view = child.GetView(f);
            Assert.True(view.Layers[0]!.Hidden.Live!.IsSealed);
            var rows = ScanVisible(view);
            Assert.Single(rows);
            Assert.Equal(new uint[] { a, 5 }, rows[0].Cells[..2]);
            Assert.Equal(1, rows[0].Layer);
            Assert.Equal(5u, Row(child, f, a)![1]);
            LookupPaths.Check(child);
        }

        [Fact]
        public void ARuleOverTheViewDerivesOnlyTheChildsValue()
        {
            // R1-1, rule half: root f(a)=1, frozen; the child writes f(a)=5 (a merge that grows no union-find); a rule over
            // f in the child derives g(a,5) only, never g(a,1) from the hidden root row.
            var (store, f, child, a) = Setup();
            var g = store.AddTable(Relation(2), name: "g");
            Insert(child, f, a, 5, 0);
            LayeredRuleUtil.Run(child, rsb =>
            {
                var q = rsb.NewRule();
                var x = q.NewVariable("x");
                var v = q.NewVariable("v");
                q.AddAtom(f, [x, v, QueryEntry.Wildcard()]);
                var rb = q.Build();
                var ts = rb.ReadCounter(store.ClockCounter);
                rb.Insert(g, [x, v, ts]);
                rb.Build("f to g");
            });

            Assert.Equal([$"{a},5"], LayeredRuleUtil.Visible(child, g));
            Assert.Equal(0, child.Tables!.UnsealedCount);
            child.CheckInvariants();
            LookupPaths.Check(child);
        }

        [Fact]
        public void ADeletedAncestorRowIsAbsentAtOnce()
        {
            // R1-1, delete variant: after StageRemove(f, [a]), TryGetRow(f, [a]) is absent.
            var (_, f, child, a) = Setup();
            Remove(child, f, a);
            Assert.Equal(0, child.Tables!.UnsealedCount);
            Assert.Null(Row(child, f, a));
            Assert.Empty(ScanVisible(child.GetView(f)));
            Assert.True(child.GetView(f).Layers[0]!.Hidden.Live!.IsSealed);
            LookupPaths.Check(child);
        }

        [Fact]
        public void EveryWriteLeavesEverySetSealed()
        {
            var store = new Store(ParallelHeuristics.Serial);
            var f = store.AddTable(MaxFunction(1), name: "f");
            var g = store.AddTable(Relation(2), name: "g");
            var root = store.Root;
            var ids = Enumerable.Range(0, 30).Select(_ => root.NewId()).ToArray();
            root.Write(state =>
            {
                foreach (var id in ids)
                {
                    state.StageInsert(f, [id, 1, state.Timestamp]);
                    state.StageInsert(g, [id, id, state.Timestamp]);
                }

                return 0;
            });

            root.Freeze();
            var child = root.CreateChild();
            var random = new Random(3);
            for (var batch = 0; batch < 40; batch++)
            {
                child.Write(state =>
                {
                    for (var n = random.Next(1, 6); n > 0; n--)
                    {
                        var id = ids[random.Next(ids.Length)];
                        if (random.Next(3) == 0)
                        {
                            if (random.Next(2) == 0)
                                state.StageRemove(f, new[] { id });
                            else
                                state.StageRemove(g, new[] { id, id });
                        }
                        else
                        {
                            state.StageInsert(f, [id, (uint)random.Next(10), state.Timestamp]);
                        }
                    }

                    return 0;
                });

                Assert.Equal(0, child.Tables!.UnsealedCount);
                LookupPaths.Check(child);
            }

            Assert.True(child.Tables!.Statistics.RowsHidden > 0);
        }

        [Fact]
        public void MergeTableSealsToo()
        {
            var (_, f, child, a) = Setup();
            var db = child.Database;
            var state = db.CreateExecutionState(child.Store.ReadClock());
            state.StageInsert(f, [a, 9, state.Timestamp]);
            state.Flush();
            db.MergeTable(f, state.Timestamp);
            Assert.Equal(0, child.Tables!.UnsealedCount);
            Assert.True(child.Tables.TryGetLayer(f)!.HiddenOver(0)!.IsSealed);

            // The notification MergeTable leaves behind is merged by the next rebuild.
            child.Rebuild();
            Assert.Equal(9u, Row(child, f, a)![1]);
            LookupPaths.Check(child);
        }

    }

}

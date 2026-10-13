using System;
using System.Collections.Generic;
using System.Linq;

using Xunit;

using static Eggnog.Relations.Tests.LayeredTestUtil;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// The guards of a rule run in a child: a rule set whose pinned views are stale is refused, layers its search
    /// creates wait for the merge, an exception faults the child, an action cannot write the context in the middle of
    /// the search, and the union-find is read through lookups only.
    /// </summary>
    public class ChildRunGuardTests
    {

        /// <summary>
        /// A store with <c>f(x)</c> holding a few ids at the root, an empty <c>g(x)</c>, and a child of the root, which
        /// is left writable so a test can change it after the child exists.
        /// </summary>
        static (Store Store, TableId F, TableId G, Context Root, Context Child) Setup()
        {
            var store = new Store(ParallelHeuristics.Serial);
            var f = store.AddTable(Relation(1), name: "f");
            var g = store.AddTable(Relation(1), name: "g");
            var root = store.Root;
            var ids = Enumerable.Range(0, 5).Select(_ => root.NewId()).ToArray();
            root.Write(state =>
            {
                foreach (var id in ids)
                    state.StageInsert(f, [id, state.Timestamp]);

                return 0;
            });

            return (store, f, g, root, root.CreateChild());
        }

        /// <summary>
        /// Builds <c>f(x) → g(x)</c> in <paramref name="context"/>, with <paramref name="external"/> called on every
        /// match.
        /// </summary>
        static RuleSet Build(Store store, TableId f, TableId g, Context context, ExternalFunctionId? external = null)
        {
            var rsb = context.Database.NewRuleSet();
            var q = rsb.NewRule();
            var x = q.NewVariable("x");
            q.AddAtom(f, [x, QueryEntry.Wildcard()]);
            var rb = q.Build();
            var ts = rb.ReadCounter(store.ClockCounter);
            if (external is { } e)
                rb.CallExternal(e, [x]);

            rb.Insert(g, [x, ts]);
            rb.Build("f to g");
            return rsb.Build();
        }

        /// <summary>
        /// A rule set built before the child's own merge changed its view is refused by both run entry points without
        /// faulting the child, since its headers and snapshot describe the old rows; built again, it runs.
        /// </summary>
        [Fact]
        public void ARuleSetWhoseViewsChangedSinceItWasBuiltIsRefused()
        {
            var (store, f, g, _, child) = Setup();
            using (child)
            {
                using var rs = Build(store, f, g, child);

                // The child's own merge drops its view of f: the rule set's headers and snapshot describe the old rows.
                Insert(child, f, child.NewId(), 0);
                Assert.Throws<InvalidOperationException>(() => child.Database.RunRuleSet(rs, ReportLevel.TimeOnly, store.ReadClock()));
                Assert.Throws<InvalidOperationException>(() => child.Database.ExecuteRuleSet(rs, ReportLevel.TimeOnly, store.ReadClock()));
                Assert.False(child.IsFaulted);

                // Built again, it runs over the new rows.
                using var again = Build(store, f, g, child);
                child.Database.RunRuleSet(again, ReportLevel.TimeOnly, store.ReadClock());
                Assert.Equal(6, LayeredRuleUtil.Visible(child, g).Count);
            }
        }

        /// <summary>
        /// A rule set is refused, without faulting the child, while the parent has changed since the child caught up.
        /// </summary>
        [Fact]
        public void ARuleSetIsRefusedWhileTheChildIsBehindItsParent()
        {
            var (store, f, g, root, child) = Setup();
            using (child)
            {
                using var rs = Build(store, f, g, child);

                // The parent changes after the rule set was built: the child's views are not current until it catches
                // up.
                Insert(root, f, root.NewId(), 0);
                Assert.False(child.IsViewCurrent);
                Assert.Throws<InvalidOperationException>(() => child.Database.RunRuleSet(rs, ReportLevel.TimeOnly, store.ReadClock()));
                Assert.False(child.IsFaulted);
                child.Rebuild();
                Assert.Empty(LayeredRuleUtil.Visible(child, g));
            }
        }

        /// <summary>
        /// A layer the search creates by staging into a table is adopted into the table map at the merge, not during
        /// the search.
        /// </summary>
        [Fact]
        public void LayersCreatedDuringTheSearchWaitForTheMerge()
        {
            var (store, f, g, _, child) = Setup();
            using (child)
            {
                using var rs = Build(store, f, g, child);
                Assert.Null(child.Tables!.TryGetLayer(g));
                var executed = child.Database.ExecuteRuleSet(rs, ReportLevel.TimeOnly, store.ReadClock());

                // The search staged into g and so created its layer, which enters the table map at the merge only.
                var layer = child.Tables!.TryGetLayer(g);
                Assert.NotNull(layer);
                Assert.False(layer!.IsAdopted);
                child.Database.MergeRuleSet(rs, executed, store.ReadClock());
                Assert.True(layer.IsAdopted);
                Assert.Equal(5, LayeredRuleUtil.Visible(child, g).Count);
                child.CheckInvariants();
            }
        }

        /// <summary>
        /// An exception thrown by an external function during a run escapes and faults the child, which then refuses
        /// writes.
        /// </summary>
        [Fact]
        public void AnExceptionEscapingARunFaultsTheChild()
        {
            var (store, f, g, _, child) = Setup();
            using (child)
            {
                var thrower = store.Root.Database.AddExternalFunction(ExternalFunction.Create((ExecutionState state, ReadOnlySpan<uint> args, out uint result) =>
                {
                    throw new InvalidOperationException("an external function failed");
                }));

                using var rs = Build(store, f, g, child, thrower);
                Assert.Throws<InvalidOperationException>(() => child.Database.RunRuleSet(rs, ReportLevel.TimeOnly, store.ReadClock()));

                // Only Dispose is allowed now.
                Assert.True(child.IsFaulted);
                Assert.ThrowsAny<InvalidOperationException>(() => Insert(child, f, child.NewId(), 0));
            }
        }

        /// <summary>
        /// A rule in a child may look the union-find up, and the lookup answers from the child's own unions.
        /// </summary>
        [Fact]
        public void AChildRuleMayLookUpTheUnionFind()
        {
            var (store, f, g, _, child) = Setup();
            using (child)
            {
                // A child reads the union-find through lookups only: its snapshot holds no view of it, and the lookups
                // read the child's own union-find.
                var a = child.NewId();
                var b = child.NewId();
                Insert(child, f, a, 0);
                Assert.True(child.Union(a, b));
                child.Rebuild();
                var report = LayeredRuleUtil.Run(child, rsb =>
                {
                    var q = rsb.NewRule();
                    var x = q.NewVariable("x");
                    q.AddAtom(f, [x, QueryEntry.Wildcard()]);
                    var rb = q.Build();
                    var ts = rb.ReadCounter(store.ClockCounter);
                    var canonical = rb.LookupWithDefault(store.UnionFindTable, [new Value(b)], x, new ColumnId(1));
                    rb.AssertEq(canonical, QueryEntry.IdConst(new Value(a)));
                    rb.Insert(g, [x, ts]);
                    rb.Build("look up the union-find");
                });

                Assert.Equal(6, report.NumMatches("look up the union-find"));
                Assert.Equal(6, LayeredRuleUtil.Visible(child, g).Count);
                child.CheckInvariants();
            }
        }

        /// <summary>
        /// A write to the child from inside a rule's action is refused on every match, and the run itself completes.
        /// </summary>
        [Fact]
        public void AnActionCannotWriteTheChildDuringTheSearch()
        {
            var (store, f, g, _, child) = Setup();
            using (child)
            {
                var refused = new List<string>();
                var nested = store.Root.Database.AddExternalFunction(ExternalFunction.Create((ExecutionState state, ReadOnlySpan<uint> args, out uint result) =>
                {
                    var id = args[0];
                    result = id;
                    try
                    {
                        child.Write(s =>
                        {
                            s.StageInsert(g, [id, s.Timestamp]);
                            return 0;
                        });
                    }
                    catch (InvalidOperationException)
                    {
                        refused.Add("write");
                    }

                    return true;
                }));

                using var rs = Build(store, f, g, child, nested);
                child.Database.RunRuleSet(rs, ReportLevel.TimeOnly, store.ReadClock());
                Assert.Equal(5, refused.Count);
                Assert.Equal(5, LayeredRuleUtil.Visible(child, g).Count);
                child.CheckInvariants();
            }
        }

        /// <summary>
        /// A child that asserts nothing sees what its parent sees. A rule over the union-find run in a child either
        /// sees the root's unions or is refused, since a <c>$uf</c> atom in a child is unsupported; it never reads the
        /// child's own union-find rows as if they were its view.
        /// </summary>
        [Fact]
        public void AChildRuleOverTheUnionFindDoesNotSilentlyReadThePlaceholder()
        {
            var store = new Store(ParallelHeuristics.Serial);
            var root = store.Root;
            var uf = store.UnionFindTable;
            var marker = store.AddTable(new SortedWritesTable(1, 2, C(1), [], new KeepCurrentMerge(), ParallelHeuristics.Serial), name: "marker");

            var a = root.NewId();
            var b = root.NewId();
            var e = root.BeginEpoch();
            root.Database.WithExecutionState(s => { s.StageInsert(uf, [b, a, e]); return 0; }, e);
            root.Database.MergeAll(e);
            root.EndEpoch();
            Assert.Equal(1, store.RootUnionFind.UnionFind.Count);

            var child = root.CreateChild();

            int RunOver(Database db)
            {
                var rsb = db.NewRuleSet();
                var q = rsb.NewRule();
                var x = q.NewVariable("x");
                var y = q.NewVariable("y");
                var t = q.NewVariable("t");
                q.AddAtom(uf, [x, y, t]);
                var rb = q.Build();
                rb.Build("over-uf");
                using var rs = rsb.Build();
                return (int)db.RunRuleSet(rs, ReportLevel.WithPlan).NumMatches("over-uf");
            }

            Assert.Equal(1, RunOver(root.Database));

            int childMatches;
            try
            {
                childMatches = RunOver(child.Database);
            }
            catch (Exception ex) when (ex is NotSupportedException or InvalidOperationException or KeyNotFoundException)
            {
                return; // refused: acceptable
            }

            Assert.Equal(1, childMatches);
            _ = marker;
        }

    }

}

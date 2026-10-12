using System;
using System.Collections.Generic;
using System.Linq;

using Xunit;

using static Eggnog.Relations.Tests.LayeredTestUtil;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// The guards of a rule run in a child (colored-contexts spec §8.3, §8.4, §9.1, X12, X21, X37, I29, I34): a rule set
    /// whose pinned views are stale is refused, layers its search creates wait for the merge, an exception faults the
    /// child, and an action cannot write the context in the middle of the search.
    /// </summary>
    public class ChildRunGuardTests
    {

        /// <summary>
        /// A store with <c>f(x)</c> holding a few ids at the root, an empty <c>g(x)</c>, and a child of the root, which is
        /// left writable so a test can change it after the child exists.
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
        /// Builds <c>f(x) → g(x)</c> in <paramref name="context"/>, with <paramref name="external"/> called on every match.
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

        [Fact]
        public void ARuleSetIsRefusedWhileTheChildIsBehindItsParent()
        {
            var (store, f, g, root, child) = Setup();
            using (child)
            {
                using var rs = Build(store, f, g, child);

                // The parent changes after the rule set was built: the child's views are not current until it catches up.
                Insert(root, f, root.NewId(), 0);
                Assert.False(child.IsViewCurrent);
                Assert.Throws<InvalidOperationException>(() => child.Database.RunRuleSet(rs, ReportLevel.TimeOnly, store.ReadClock()));
                Assert.False(child.IsFaulted);
                child.Rebuild();
                Assert.Empty(LayeredRuleUtil.Visible(child, g));
            }
        }

        [Fact]
        public void LayersCreatedDuringTheSearchWaitForTheMerge()
        {
            var (store, f, g, _, child) = Setup();
            using (child)
            {
                using var rs = Build(store, f, g, child);
                Assert.Null(child.Tables!.TryGetLayer(g));
                var executed = child.Database.ExecuteRuleSet(rs, ReportLevel.TimeOnly, store.ReadClock());

                // X37: the search staged into g and so created its layer, which enters the table map at the merge only.
                var layer = child.Tables!.TryGetLayer(g);
                Assert.NotNull(layer);
                Assert.False(layer!.IsAdopted);
                child.Database.MergeRuleSet(rs, executed, store.ReadClock());
                Assert.True(layer.IsAdopted);
                Assert.Equal(5, LayeredRuleUtil.Visible(child, g).Count);
                child.CheckInvariants();
            }
        }

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

                // X12: only Dispose is allowed now.
                Assert.True(child.IsFaulted);
                Assert.ThrowsAny<InvalidOperationException>(() => Insert(child, f, child.NewId(), 0));
            }
        }

        [Fact]
        public void AChildRuleMayLookUpTheUnionFind()
        {
            var (store, f, g, _, child) = Setup();
            using (child)
            {
                // A child reads the union-find through lookups only (X23): its snapshot holds no view of it, and the
                // lookups read the child's own union-find.
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

    }

}

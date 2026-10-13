using System;
using System.Linq;
using System.Threading.Tasks;

using Xunit;

using static Eggnog.Relations.Tests.BridgeTestUtil;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// The bridge's freeze (colored-contexts spec §10.8, §17 M10.3; rule-layer E10): it flushes, rebuilds, refuses a
    /// recorded failure, plans the rules descendants will run (all live rules, or exactly the given ones), warms their
    /// indexes and freezes the storage context. Named apart from the storage <c>FreezeTests</c>.
    /// </summary>
    public class BridgeFreezeTests
    {

        [Fact]
        public void FreezeFlushesRebuildsAndPlansEveryLiveRule()
        {
            var g = new ContextGraph();
            var planned = g.EdgeToPath(name: "planned");
            var unplanned = g.Transitive(name: "unplanned");
            var freed = g.EdgeToPath(name: "freed");
            g.EGraph.FreeRule(freed);
            g.Root.RunRules(planned);
            var plan = g.EGraph.GetRuleInfo(planned).CachedPlan;
            Assert.NotNull(plan);
            Assert.Null(g.EGraph.GetRuleInfo(unplanned).CachedPlan);

            var a = g.Root.FreshId();
            var b = g.Root.FreshId();
            g.Assert(g.Root, g.Edge, a, b);
            g.Root.StageUnion(a, b);
            g.Root.Freeze();

            Assert.True(g.Root.IsFrozen);
            Assert.True(g.Root.IsClean);
            Assert.Equal([$"{a.Rep} {a.Rep}"], ContextGraph.Keys(g.Root, g.Edge));
            Assert.Same(plan, g.EGraph.GetRuleInfo(planned).CachedPlan);
            Assert.NotNull(g.EGraph.GetRuleInfo(unplanned).CachedPlan);
            Assert.True(g.EGraph.GetRuleInfo(unplanned).AllowInheritance);
            g.Root.CheckInvariants();

            // A second freeze does nothing; writes are refused.
            g.Root.Freeze();
            Assert.Throws<InvalidOperationException>(() => g.Root.StageRow(g.Mark, [a, g.Unit]));
            Assert.Throws<InvalidOperationException>(() => g.Root.RunRules(planned));
            Assert.Throws<InvalidOperationException>(() => g.Root.FlushUpdates());
            Assert.Throws<InvalidOperationException>(() => g.EGraph.RunRules(planned));
        }

        /// <summary>
        /// Rule-layer E10: given a collection, the freeze plans exactly those rules (and only those without a plan);
        /// the others stay unplanned, to be planned on the statistics of the context that first runs them.
        /// </summary>
        [Fact]
        public void FreezeWithACollectionPlansExactlyThoseRules()
        {
            var g = new ContextGraph();
            var chosen = g.EdgeToPath(name: "chosen");
            var other = g.Transitive(name: "other");
            g.Root.Freeze([]);
            Assert.Null(g.EGraph.GetRuleInfo(chosen).CachedPlan);
            Assert.Null(g.EGraph.GetRuleInfo(other).CachedPlan);

            using var child = g.Root.CreateChild();
            child.Freeze([chosen]);
            Assert.NotNull(g.EGraph.GetRuleInfo(chosen).CachedPlan);
            Assert.Null(g.EGraph.GetRuleInfo(other).CachedPlan);
            Assert.Throws<ArgumentException>(() => g.Root.CreateChild().Freeze([new EGraphRuleId(99)]));
        }

        /// <summary>
        /// The freeze warms the indexes of the planned rules' plans (§8.1, §9.10): a frozen root's catalogs hold them,
        /// current, before any child runs.
        /// </summary>
        [Fact]
        public void FreezeWarmsThePlansIndexes()
        {
            var g = new ContextGraph();
            var rule = g.Transitive();
            var ids = Enumerable.Range(0, 64).Select(_ => g.Root.FreshId()).ToArray();
            for (var i = 0; i + 1 < ids.Length; i++)
            {
                g.Assert(g.Root, g.Edge, ids[i], ids[i + 1]);
                g.Assert(g.Root, g.Path, ids[i], ids[i + 1]);
            }

            g.Root.FlushUpdates();
            g.Root.Freeze();
            var template = g.EGraph.GetRuleInfo(rule).CachedPlan!.Plan.Plan;
            var keys = FreezeWarmup.From([template], g.EGraph.Database).Keys.ToArray();
            Assert.NotEmpty(keys);
            foreach (var key in keys)
            {
                var info = g.EGraph.Database.GetTableInfo(key.Table);
                var built = false;
                if (key.Columns.Length == 1)
                {
                    foreach (var entry in info.ColumnIndexes.Entries)
                        built |= entry.Index.TryGet(out var index) && index.NeedsRefresh(info.Table) == false;
                }
                else
                {
                    foreach (var entry in info.Indexes.Entries)
                        built |= entry.Index.TryGet(out var index) && index.NeedsRefresh(info.Table) == false;
                }

                Assert.True(built, $"no current index on table {key.Table} columns [{string.Join(", ", key.Columns)}]");
            }

            // Concurrent children of the frozen root run the rule on the warm, shared indexes.
            var results = new int[8];
            Parallel.For(0, results.Length, i =>
            {
                using var child = g.Root.CreateChild();
                child.Saturate([rule], 100);
                results[i] = child.TableSize(g.Path);
            });
            Assert.All(results, n => Assert.Equal(63 * 64 / 2, n));
        }

        /// <summary>
        /// A failure recorded and not taken, before the freeze or by its flush or rebuild, refuses the freeze with
        /// <see cref="PanicException"/>: the context stays unfrozen and usable, and freezes once the failure is taken
        /// (§10.8 step 3, §13 edge 22).
        /// </summary>
        [Fact]
        public void AFailureRefusesTheFreezeWithoutFreezing()
        {
            var g = new ContextGraph();
            var a = g.Root.FreshId();
            g.EGraph.AddValues([(g.Low, [a, g.Int(3)])]);
            g.Root.WithExecutionState(state =>
            {
                state.ReportFailure("pending");
                return 0;
            });
            var error = Assert.Throws<PanicException>(() => g.Root.Freeze());
            Assert.Equal("pending", error.PanicMessage);
            Assert.False(g.Root.IsFrozen);
            g.Root.Freeze();
            Assert.True(g.Root.IsFrozen);

            // A child whose flush records a merge failure: an assert-eq conflict on a relation's output.
            var g2 = new ContextGraph();
            var keyed = g2.EGraph.AddTable(new FunctionConfig
            {
                Schema = [ColumnTy.Id, g2.I64],
                Default = DefaultVal.Fail,
                Merge = MergeFn.AssertEq,
                Name = "keyed",
            });
            var b = g2.Root.FreshId();
            g2.Root.StageRow(keyed, [b, g2.Int(1)]);
            g2.Root.FlushUpdates();
            g2.Root.Freeze();
            using var child = g2.Root.CreateChild();
            child.StageRow(keyed, [b, g2.Int(2)]);
            Assert.Throws<PanicException>(() => child.Freeze());
            Assert.False(child.IsFrozen);
            Assert.False(child.IsFaulted);
            child.Freeze();
            Assert.True(child.IsFrozen);
            child.CheckInvariants();
        }

        [Fact]
        public void FreezeNeedsAFrozenParentAndChangesNothingOtherwise()
        {
            var g = new ContextGraph();
            using var child = g.Root.CreateChild();
            var a = child.FreshId();
            g.Assert(child, g.Mark, a);
            Assert.Throws<InvalidOperationException>(() => child.Freeze());
            Assert.False(child.IsFrozen);
            Assert.False(child.IsClean);
            child.FlushUpdates();
            Assert.Equal([$"{a.Rep}"], ContextGraph.Keys(child, g.Mark));
        }

        /// <summary>
        /// A frozen chain answers reads on any thread, and its children run concurrently, each in a domain of its own.
        /// </summary>
        [Fact]
        public void FrozenContextsAreSharedAcrossThreads()
        {
            var g = new ContextGraph();
            var rule = g.EdgeToPath();
            var ids = Enumerable.Range(0, 8).Select(_ => g.Root.FreshId()).ToArray();
            g.Assert(g.Root, g.Edge, ids[0], ids[1]);
            g.Root.Freeze();
            var middle = g.Root.CreateChild();
            g.Assert(middle, g.Edge, ids[2], ids[3]);
            middle.Freeze();

            var sizes = new int[16];
            Parallel.For(0, sizes.Length, i =>
            {
                using var leaf = middle.CreateChild();
                g.Assert(leaf, g.Edge, ids[4 + i % 4], ids[i % 4]);
                leaf.RunRules(rule);
                sizes[i] = leaf.TableSize(g.Path);
                leaf.CheckInvariants();
            });
            Assert.All(sizes, n => Assert.Equal(3, n));
            Assert.Empty(ContextGraph.Keys(middle, g.Path));
            Assert.Equal(2, middle.TableSize(g.Edge));
            middle.Dispose();
        }

    }

}

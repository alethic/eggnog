using System.Linq;

using Xunit;

using static Eggnog.Relations.Tests.LayeredTestUtil;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// Review probe (root cost, colored-contexts spec §14.1 "Id constants (Store-backed root)": per rule per run,
    /// O(constants) finds and one compare). A store-backed root has one current K per rule at a time, so a rule whose
    /// constant changes its canonical form must keep paying O(constants) per run, however many times it changed.
    /// </summary>
    public class RootConstantVariantCostTests
    {

        [Fact]
        public void TheRootsCurrentVariantStaysCachedAfterManyCanonicalChanges()
        {
            var store = new Store(ParallelHeuristics.Serial);
            var r = store.AddTable(Relation(2), name: "r");
            var s = store.AddTable(Relation(2), name: "s");
            var root = store.Root;
            var x = root.NewId();
            var smaller = Enumerable.Range(0, 20).Select(_ => root.NewId()).ToArray();
            var k = root.NewId();
            Insert(root, r, x, k, 0);

            // r(v, k) → s(v, k), planned once and cached, as the bridge does.
            CachedPlan cached;
            {
                var rsb = root.Database.NewRuleSet();
                var q = rsb.NewRule();
                var v = q.NewVariable("v");
                q.AddAtom(r, [v, new Value(k), QueryEntry.Wildcard()]);
                var rb = q.Build();
                var ts = rb.ReadCounter(store.ClockCounter);
                rb.Insert(s, [v, new Value(k), ts]);
                var rule = rb.Build("r to s");
                using var rs = rsb.Build();
                cached = rs.BuildCachedPlan(rule);
            }

            PlanTemplate Instantiate()
            {
                var rsb = root.Database.NewRuleSet();
                var id = rsb.AddRuleFromCachedPlan(cached);
                Assert.NotNull(id);
                using var rs = rsb.Build();
                return rs[id!.Value].Template;
            }

            // k's class takes a smaller canonical id twenty times; after each, two runs with the same K.
            for (var i = smaller.Length - 1; i >= 0; i--)
            {
                Assert.True(root.Union(k, smaller[i]));
                root.Rebuild();
                Assert.Equal(smaller[i], root.Find(k));
                var first = Instantiate();
                var before = System.GC.GetAllocatedBytesForCurrentThread();
                var second = Instantiate();
                var bytes = System.GC.GetAllocatedBytesForCurrentThread() - before;
                System.IO.File.AppendAllText(System.Environment.GetEnvironmentVariable("RC_OUT") ?? System.IO.Path.Combine(System.AppContext.BaseDirectory, "rootcost.txt"), $"variant probe: change {smaller.Length - i}, {cached.VariantCount} variants cached, {bytes} B to build the rule set again{System.Environment.NewLine}");
                Assert.True(ReferenceEquals(first, second), $"after {smaller.Length - i} canonical changes ({cached.VariantCount} variants cached) the root rebuilds its variant on every run");
            }
        }

    }

}

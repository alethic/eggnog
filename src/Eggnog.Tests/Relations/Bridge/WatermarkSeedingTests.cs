using System;

using Xunit;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// Watermark seeding: a context without progress for a rule seeds it lazily from the nearest ancestor with
    /// progress as <c>min(W_a, CreatedAt of the ancestor's child on the path)</c>, refused when the executed constants
    /// differ, for rules that may not inherit, and for contexts that do not inherit; generations keep a reused rule id
    /// from inheriting a freed rule's progress.
    /// </summary>
    public class WatermarkSeedingTests
    {

        static uint Watermark(EGraphContext context, EGraphRuleId rule)
        {
            var info = context.Owner.GetRuleInfo(rule);
            var plan = info.GetOrBuildCachedPlan(context.Context.Database).Plan;
            var constants = CanonicalConstants.Compute(context.Context.Database.View, plan.IdConstants, plan.Actions.IdConstants, plan.WrittenConstants);
            return context.GetWatermark(rule, info, constants);
        }

        /// <summary>
        /// A grandchild C of the root G whose parent P never ran the rule seeds from G's progress, bounded by P's
        /// creation: <c>min(W_G, c_P)</c>. With G's run after P's creation the bound decides.
        /// </summary>
        [Fact]
        public void APNeverRanSeedsFromTheGrandparentBoundedByThePathsCreation()
        {
            var g = new ContextGraph();
            var rule = g.EdgeToPath();
            var a = g.Root.FreshId();
            var b = g.Root.FreshId();
            g.Root.AddValues([(g.Edge, [a, b, g.Unit])]);

            using var p = g.Root.CreateChild();
            var createdP = p.CreatedAt;

            // The root runs after P exists, at a later epoch: W_G > c_P.
            g.Assert(g.Root, g.Mark, a);
            g.Root.FlushUpdates();
            g.Root.RunRules(rule);
            var wg = g.Root.CommittedLastRunAt(rule, g.EGraph.GetRuleInfo(rule).Generation);
            Assert.True(wg > createdP);

            using var c = p.CreateChild();
            Assert.Equal(createdP, Watermark(c, rule));
            Assert.Equal(createdP, Watermark(p, rule));

            // A rule G ran before P existed seeds with W_G itself.
            var early = g.Transitive();
            var g2 = new ContextGraph();
            var rule2 = g2.EdgeToPath();
            g2.Root.RunRules(rule2);
            var wg2 = g2.Root.CommittedLastRunAt(rule2, g2.EGraph.GetRuleInfo(rule2).Generation);
            using var p2 = g2.Root.CreateChild();
            using var c2 = p2.CreateChild();
            Assert.True(wg2 < p2.CreatedAt);
            Assert.Equal(wg2, Watermark(c2, rule2));
            Assert.Equal(0u, Watermark(c, early));

            // C's own run replaces the seed with its own progress.
            c.RunRules(rule);
            Assert.True(Watermark(c, rule) > createdP);
            c.CheckInvariants();
            p.CheckInvariants();
        }

        /// <summary>
        /// The seeded run is semi-naive over the ancestor's old rows: a child that asserts nothing matches nothing the
        /// root's run already applied, and still finds its own new rows.
        /// </summary>
        [Fact]
        public void ASeededRunSkipsTheAncestorsAppliedMatches()
        {
            var g = new ContextGraph();
            var rule = g.EdgeToPath();
            var ids = new Value[4];
            for (var i = 0; i < ids.Length; i++)
                ids[i] = g.Root.FreshId();

            g.Root.AddValues([(g.Edge, [ids[0], ids[1], g.Unit])]);
            g.Root.RunRules(rule);
            g.Root.Freeze();

            using var child = g.Root.CreateChild();
            Assert.Equal(0, child.RunRules(rule).RuleSetReport.NumMatches("edge-path"));
            g.Assert(child, g.Edge, ids[2], ids[3]);
            child.FlushUpdates();
            Assert.Equal(1, child.RunRules(rule).RuleSetReport.NumMatches("edge-path"));
            Assert.Equal(["0 1", "2 3"], ContextGraph.Keys(child, g.Path));

            // Unseeded, the child re-matches the root's row too, and derives the same view.
            using var unseeded = g.Root.CreateChild(new ContextOptions { InheritWatermarks = false });
            Assert.Equal(1, unseeded.RunRules(rule).RuleSetReport.NumMatches("edge-path"));
            Assert.Equal(["0 1"], ContextGraph.Keys(unseeded, g.Path));
        }

        /// <summary>
        /// A watermark is used only with the constants it ran with: the rule's executed constants K, the canonical
        /// forms of its Id constants in the running context, are kept with its progress. After a constant loses a union
        /// in the child, K differs, and the child runs the rule fully, matching the constant's class.
        /// </summary>
        [Fact]
        public void AConstantThatLostAUnionInTheChildRefusesTheSeed()
        {
            var g = new ContextGraph();
            var low = g.Root.FreshId();
            var high = g.Root.FreshId();
            Assert.True(low.Rep < high.Rep);

            // mark(high) => path(high, high): a constant id in the query and in the action.
            var rb = g.EGraph.NewRule("mark-high", true);
            rb.QueryTable(g.Mark, [RuleEntry.Const(high, ColumnTy.Id), rb.NewVar(g.UnitType)], false);
            rb.Set(g.Path, [RuleEntry.Const(high, ColumnTy.Id), RuleEntry.Const(high, ColumnTy.Id), RuleEntry.Const(g.Unit, g.UnitType)]);
            var rule = rb.Build();

            g.Root.AddValues([(g.Mark, [low, g.Unit])]);
            g.Root.RunRules(rule);
            Assert.Empty(ContextGraph.Keys(g.Root, g.Path));
            g.Root.Freeze();

            using var child = g.Root.CreateChild();
            var seeded = Watermark(child, rule);
            Assert.True(seeded > 0);

            // high loses to low in the child: K becomes [low, low, low], so the seed is refused and the old row
            // matches.
            child.StageUnion(low, high);
            child.FlushUpdates();
            Assert.Equal(0u, Watermark(child, rule));
            var report = child.RunRules(rule);
            Assert.Equal(1, report.RuleSetReport.NumMatches("mark-high"));
            Assert.Equal([$"{low.Rep} {low.Rep}"], ContextGraph.Keys(child, g.Path));
            child.CheckInvariants();
        }

        /// <summary>
        /// A reused rule id gets a new generation: neither the context's own stale progress nor an ancestor's applies
        /// to the rule that reuses the id.
        /// </summary>
        [Fact]
        public void ARuleIdReuseNeverInheritsTheFreedRulesProgress()
        {
            var g = new ContextGraph();
            var first = g.EdgeToPath(name: "first");
            g.Root.RunRules(first);
            Assert.True(Watermark(g.Root, first) > 0);
            var generation = g.EGraph.GetRuleInfo(first).Generation;

            g.EGraph.FreeRule(first);
            var second = g.EdgeToPath(name: "second");
            Assert.Equal(first, second);
            Assert.Equal(generation + 1, g.EGraph.GetRuleInfo(second).Generation);
            Assert.Equal(0u, Watermark(g.Root, second));
            Assert.Equal(0u, g.EGraph.GetRuleInfo(second).LastRunAt);

            g.Root.Freeze();
            using var child = g.Root.CreateChild();
            Assert.Equal(0u, Watermark(child, second));
        }

        /// <summary>
        /// Rules that may not inherit run fully in a child: naive rules, rules whose actions read a table that is not a
        /// constructor, and every rule in a context created with <see cref="ContextOptions.InheritWatermarks"/> off; a
        /// rule whose only lookup is a constructor's does inherit.
        /// </summary>
        [Fact]
        public void NonInheritingRulesStartAtZero()
        {
            var g = new ContextGraph();
            var naive = g.EdgeToPath(semiNaive: false, name: "naive");
            var semi = g.EdgeToPath(name: "semi");

            // edge(x, y) => path(x, y) where low(x) holds: a lookup of a lattice function, which a child can lower.
            var rb = g.EGraph.NewRule("reads-low", true);
            var x = rb.NewVar(ColumnTy.Id);
            var y = rb.NewVar(ColumnTy.Id);
            rb.QueryTable(g.Edge, [x, y, rb.NewVar(g.UnitType)], false);
            rb.Lookup(g.Low, [x], () => "no low");
            rb.Set(g.Path, [x, y, RuleEntry.Const(g.Unit, g.UnitType)]);
            var readsLow = rb.Build();

            // edge(x, y) => path(add(x, y), y): a constructor lookup, query-determined.
            rb = g.EGraph.NewRule("mints", true);
            x = rb.NewVar(ColumnTy.Id);
            y = rb.NewVar(ColumnTy.Id);
            rb.QueryTable(g.Edge, [x, y, rb.NewVar(g.UnitType)], false);
            var sum = rb.Lookup(g.Add, [x, y], () => "");
            rb.Set(g.Path, [sum, y, RuleEntry.Const(g.Unit, g.UnitType)]);
            var mints = rb.Build();

            var a = g.Root.FreshId();
            var b = g.Root.FreshId();
            g.Root.AddValues([(g.Edge, [a, b, g.Unit]), (g.Low, [a, g.Int(3)]), (g.Low, [b, g.Int(3)])]);
            g.Root.RunRules(naive, semi, readsLow, mints);
            Assert.False(g.EGraph.GetRuleInfo(naive).AllowInheritance);
            Assert.True(g.EGraph.GetRuleInfo(semi).AllowInheritance);
            Assert.False(g.EGraph.GetRuleInfo(readsLow).AllowInheritance);
            Assert.True(g.EGraph.GetRuleInfo(mints).AllowInheritance);
            g.Root.Freeze();

            using var child = g.Root.CreateChild();
            Assert.Equal(0u, Watermark(child, naive));
            Assert.Equal(0u, Watermark(child, readsLow));
            Assert.True(Watermark(child, semi) > 0);
            Assert.True(Watermark(child, mints) > 0);

            using var unseeded = g.Root.CreateChild(new ContextOptions { InheritWatermarks = false });
            Assert.Equal(0u, Watermark(unseeded, semi));
            Assert.Equal(0u, Watermark(unseeded, mints));

            // A child that lowers low(a) re-fires the reading rule on the old match edge(a, b): an inherited watermark
            // would have skipped it, although the value its lookup reads changed in the child.
            g.Assert(child, g.Edge, b, a);
            child.StageRow(g.Low, [a, g.Int(1)]);
            child.FlushUpdates();
            var report = child.RunRules(readsLow);
            Assert.Equal(2, report.RuleSetReport.NumMatches("reads-low"));
        }

        /// <summary>
        /// A semi-naive rule without atoms fires once in its life, as in egglog; under an inherited watermark above
        /// zero it does not fire again in a child, which is correct because the ancestor's effects are visible there.
        /// Unseeded, it fires once more and writes nothing new.
        /// </summary>
        [Fact]
        public void ZeroAtomRulesDoNotRefireUnderAnInheritedWatermark()
        {
            var g = new ContextGraph();
            var a = g.Root.FreshId();
            var rb = g.EGraph.NewRule("fact", true);
            rb.Set(g.Mark, [RuleEntry.Const(a, ColumnTy.Id), RuleEntry.Const(g.Unit, g.UnitType)]);
            var rule = rb.Build();

            Assert.Equal(1, g.Root.RunRules(rule).RuleSetReport.NumMatches("fact"));
            Assert.Equal(0, g.Root.RunRules(rule).RuleSetReport.NumMatches("fact"));
            g.Root.Freeze();

            using var child = g.Root.CreateChild();
            var report = child.RunRules(rule);
            Assert.Equal(0, report.RuleSetReport.NumMatches("fact"));
            Assert.False(report.Changed);

            using var unseeded = g.Root.CreateChild(new ContextOptions { InheritWatermarks = false });
            report = unseeded.RunRules(rule);
            Assert.Equal(1, report.RuleSetReport.NumMatches("fact"));
            Assert.False(report.Changed);
            Assert.Equal([$"{a.Rep}"], ContextGraph.Keys(unseeded, g.Mark));
        }

        /// <summary>
        /// A watermark seeded through a writable parent is bounded by that parent's creation, and an intermediate
        /// context's own progress takes precedence over the root's.
        /// </summary>
        [Fact]
        public void TheNearestAncestorWithProgressDecides()
        {
            var g = new ContextGraph();
            var rule = g.EdgeToPath();
            g.Root.RunRules(rule);
            using var p = g.Root.CreateChild();
            p.RunRules(rule);
            var wp = p.CommittedLastRunAt(rule, g.EGraph.GetRuleInfo(rule).Generation);
            using var c = p.CreateChild();
            Assert.Equal(Math.Min(wp, c.CreatedAt), Watermark(c, rule));
            Assert.True(Watermark(c, rule) > Watermark(g.Root, rule));
        }

    }

}

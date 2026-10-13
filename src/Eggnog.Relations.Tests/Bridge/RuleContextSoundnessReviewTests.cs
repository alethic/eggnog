using System;

using Xunit;

using static Eggnog.Relations.Tests.BridgeTestUtil;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// Soundness probes of colors M10.3 (colored-contexts spec §10, §12): per-context rules, watermarks, host writes and
    /// faults. Each case states the property it checks and the spec item it follows from.
    /// </summary>
    public class RuleContextSoundnessReviewTests
    {

        /// <summary>
        /// I21 and I31 Case 1 at the root: a row staged at the root through <see cref="EGraphContext.WithExecutionState{TResult}"/>
        /// stays pending (X42) and merges inside the next root run, stamped with the clock read when it was staged. When a
        /// child of the writable root advances the shared clock in between, the run's timestamp T is larger than that
        /// stamp, the run commits T as the rule's watermark, and the row (merged after the search, below T) is never new
        /// to a later run. Every match over it is then lost at the root; without the child the same program derives it
        /// (<see cref="EpochTests.RootStagedWritesMergeInRun"/>).
        /// </summary>
        [Fact]
        public void ARootRowStagedRawIsMatchedAfterAChildAdvancedTheClock()
        {
            var g = new ContextGraph();
            var rule = g.EdgeToPath();
            var a = g.Root.FreshId();
            var b = g.Root.FreshId();
            using var child = g.Root.CreateChild();
            var edge = new TableAction(g.EGraph, g.Edge);
            g.Root.WithExecutionState(state =>
            {
                edge.Insert(state, [a.Rep, b.Rep, g.Unit.Rep]);
                return 0;
            });

            var staged = g.EGraph.NextTimestamp;
            child.FlushUpdates();
            Assert.True(g.EGraph.NextTimestamp > staged);

            // The first run merges the staged edge after its search; the second must see it as new.
            g.Root.RunRules(rule);
            Assert.Equal(g.Unit, g.Root.LookupId(g.Edge, [a, b]));
            g.Root.RunRules(rule);
            g.Root.RunRules(rule);
            Assert.Equal(g.Unit, g.Root.LookupId(g.Path, [a, b]));
        }

        /// <summary>
        /// §10.3 and X42 at the root: a row staged raw at the root stays pending into the next run and must merge there at
        /// the run's timestamp, so that it is new to the rule's next run (I31 Case 1). Here the rule already ran, so its
        /// semi-naive variants find no row at or after its watermark and the rule set is empty; an empty rule set merges
        /// nothing (egglog's <c>run_rule_set</c> returns before <c>merge_all</c>), yet the run commits its timestamp as the
        /// rule's watermark and advances the clock. The staged row keeps its staging stamp, which is now below the
        /// watermark, and when a later run finally merges it no run ever matches it.
        /// </summary>
        [Fact]
        public void ARootRowStagedRawBeforeAnEmptyRunIsMatchedLater()
        {
            var g = new ContextGraph();
            var rule = g.EdgeToPath();
            var a = g.Root.FreshId();
            var b = g.Root.FreshId();
            var c = g.Root.FreshId();
            g.Root.AddValues([(g.Edge, [b, c, g.Unit])]);
            g.Root.RunRules(rule);
            Assert.Equal(g.Unit, g.Root.LookupId(g.Path, [b, c]));

            var edge = new TableAction(g.EGraph, g.Edge);
            g.Root.WithExecutionState(state =>
            {
                edge.Insert(state, [a.Rep, b.Rep, g.Unit.Rep]);
                return 0;
            });

            // Runs until the staged edge is merged, then once more: the edge must have been matched by then.
            for (var i = 0; i < 4 && g.Root.LookupId(g.Edge, [a, b]) is null; i++)
                g.Root.RunRules(rule);

            // The runs found no new edge, so each built an empty rule set, merged nothing and still committed its
            // timestamp: the edge is merged only by the flush, carrying its staging stamp, below the watermark.
            g.Root.FlushUpdates();
            Assert.Equal(g.Unit, g.Root.LookupId(g.Edge, [a, b]));
            g.Root.RunRules(rule);
            g.Root.RunRules(rule);
            Assert.Equal(g.Unit, g.Root.LookupId(g.Path, [a, b]));
        }

        /// <summary>
        /// The same lost stamp reaches children through inheritance (I31 Case 2): the root's second empty run commits a
        /// watermark above the staged row's stamp, the freeze merges the row with that stamp, and a child of the frozen
        /// root seeds its watermark from the root's, so the child never matches the row either.
        /// </summary>
        [Fact]
        public void AChildOfAFrozenRootMatchesARowStagedRawBeforeAnEmptyRootRun()
        {
            var g = new ContextGraph();
            var rule = g.EdgeToPath();
            var a = g.Root.FreshId();
            var b = g.Root.FreshId();
            var c = g.Root.FreshId();
            g.Root.AddValues([(g.Edge, [b, c, g.Unit])]);
            g.Root.RunRules(rule);

            var edge = new TableAction(g.EGraph, g.Edge);
            g.Root.WithExecutionState(state =>
            {
                edge.Insert(state, [a.Rep, b.Rep, g.Unit.Rep]);
                return 0;
            });

            // Two runs with nothing new: the second commits a watermark above the staged row's stamp.
            g.Root.RunRules(rule);
            g.Root.RunRules(rule);
            g.Root.Freeze();
            Assert.Equal(g.Unit, g.Root.LookupId(g.Edge, [a, b]));

            using var child = g.Root.CreateChild();
            child.Saturate([rule], 4);
            Assert.Equal(g.Unit, child.LookupId(g.Path, [a, b]));
        }

        /// <summary>
        /// The copy oracle of <see cref="ContextRuleCopyTests"/> (§17 M10.3, X41: the fresh-root copy governs monotone
        /// programs only) is applied to programs whose rule bodies bind the output of the min lattice <c>low</c>. A host
        /// write that lowers <c>low</c> after a saturation is not monotone for such a body: facts derived from the old
        /// value persist (at a plain root too), while the fresh root replays both writes before saturating and never
        /// sees the old value. Shrunk: <c>union(#2, #1); low(#1, 3); saturate; low(#2, 0)</c> in a grandchild, with
        /// <c>low(v0, v1), num(v1, v2), num(#1, v3) =&gt; set path(add(v2, v0), v2)</c> and a union rule over edges, makes
        /// the context union <c>num(0)</c> with <c>num(3)</c>, which the fresh root never does. The generated
        /// "monotone" family must exclude this (or the copy must replay the chain's saturations), or the soak reports
        /// false failures; found by the soak from seed base 7000000.
        /// </summary>
        [Fact]
        public void TheCopyOracleHoldsForTheMonotoneFamilyAtSeed7000246()
        {
            var program = ColorProgram.Generate(7000246, new ColorFamily(true, false), ColorDifferential.Steps * 4);
            ColorDifferential.RunOrShrink(program, new ColorRunOptions(Reference: false, Copy: true));
        }

    }

}

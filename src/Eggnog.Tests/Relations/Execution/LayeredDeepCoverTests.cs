using System;
using System.Collections.Generic;
using System.Linq;

using Xunit;

using static Eggnog.Relations.Tests.LayeredTestUtil;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// Free Join covers whose rows lie in layers past the 255 a frame entry's layer byte holds, which only a chain that
    /// deep has: each row's frame refines the cover to a node of that row instead, and the cover's rows are scanned,
    /// with the rows its layers hide and its probes, as a shallow layer's are. Both run at the bottom of a chain 300
    /// below the root, whose view of <c>f</c> holds rows of the root and of two levels past 255, one of which hides the
    /// root's row of a key it rewrote.
    /// </summary>
    public class LayeredDeepCoverTests
    {

        const int Depth = 300;

        /// <summary>
        /// A chain <see cref="Depth"/> below the root, whose view of <c>f(k) → v</c> holds the root's <c>f(a) = 1</c>
        /// (hidden: level 280 wrote <c>f(a) = 5</c>), the root's <c>f(b) = 2</c> and <c>f(c) = 3</c>, level 280's
        /// <c>f(a) = 5</c> and level 300's <c>f(d) = 4</c>; and <c>q(k)</c> holding <c>b</c> at the root, <c>a</c> at level
        /// 270 and <c>d</c> at level 300. Returns the rows the bottom sees of <c>f</c>, as "k,v", and its keys in
        /// <c>q</c>.
        /// </summary>
        static (Store Store, TableId F, TableId Q, List<Context> Chain, string[] Visible, uint[] Marked) Setup()
        {
            var store = new Store(ParallelHeuristics.Serial);
            var f = store.AddTable(MaxFunction(1), name: "f");
            var q = store.AddTable(Relation(1), name: "q");
            var root = store.Root;
            var a = root.NewId();
            var b = root.NewId();
            var c = root.NewId();
            var d = root.NewId();
            Insert(root, f, a, 1, 0);
            Insert(root, f, b, 2, 0);
            Insert(root, f, c, 3, 0);
            Insert(root, q, b, 0);
            var chain = new List<Context> { root };
            for (var level = 1; level <= Depth; level++)
            {
                var child = chain[^1].CreateChild();
                chain.Add(child);
                if (level == 270)
                    Insert(child, q, a, 0);
                if (level == 280)
                    Insert(child, f, a, 5, 0);
                if (level == Depth)
                {
                    Insert(child, f, d, 4, 0);
                    Insert(child, q, d, 0);
                }
            }

            var view = chain[^1].GetView(f);
            Assert.Equal([0, 280, Depth], Enumerable.Range(0, view.Layers.Length).Where(l => view.Layers[l] is not null));
            return (store, f, q, chain, [$"{a},5", $"{b},2", $"{c},3", $"{d},4"], [a, b, d]);
        }

        /// <summary>
        /// Disposes the chain from the bottom up.
        /// </summary>
        static void Dispose(List<Context> chain)
        {
            for (var i = chain.Count - 1; i > 0; i--)
                chain[i].Dispose();
        }

        [Fact]
        public void AChunkedCoverScanPastTheLayerByteSeesWhatTheViewShows()
        {
            var (store, f, _, chain, visible, _) = Setup();
            var bottom = chain[^1];
            var log = new List<uint[]>();
            var recorder = store.Root.Database.AddExternalFunction(ExecutionTestUtil.Recorder(log));

            // Two probe-free covers of f, the first not a leaf (the second scans f again), as LayeredHandBuiltPlanTests
            // builds them: no planned rule has one.
            var rsb = bottom.Database.NewRuleSet();
            var b = rsb.NewRule();
            var x = b.NewVariable("x");
            var y = b.NewVariable("y");
            b.AddAtom(f, [x, y, QueryEntry.Wildcard()]);
            var rb = b.Build();
            rb.CallExternal(recorder, [x, y]);
            var rule = rb.Build("hand-built");
            CachedPlan cached;
            using (var rs = rsb.Build())
            {
                var planned = rs[rule];
                var template = planned.Template;
                var atom = new AtomId(0);
                JoinStage[] stages =
                [
                    new FusedIntersectStage(new ScanSpec(new SubAtom(atom, [new ColumnId(0)]), []), [new StageBinding(new ColumnId(0), x)], []),
                    new FusedIntersectStage(new ScanSpec(new SubAtom(atom, [new ColumnId(1)]), []), [new StageBinding(new ColumnId(1), y)], []),
                ];
                var plan = new SinglePlan(template.Atoms, template.VariableCount, template.Headers, template.ExistenceChecks, stages, template.Action);
                cached = new CachedPlan(plan, "hand-built", planned.Symbols, rs.GetAction(planned.Action), planned.IdConstants, []);
            }

            var report = LayeredRuleUtil.Run(bottom, rsb => rsb.AddRuleFromCachedPlan(cached));
            Assert.Equal(visible.OrderBy(s => s, StringComparer.Ordinal), ExecutionTestUtil.Distinct(log));
            Assert.Equal(4, report.NumMatches("hand-built"));
            Dispose(chain);
        }

        [Fact]
        public void AProbedCoverPastTheLayerByteJoinsRowsOfEveryLevel()
        {
            var (store, f, q, chain, visible, marked) = Setup();
            var bottom = chain[^1];
            var log = new List<uint[]>();
            var recorder = store.Root.Database.AddExternalFunction(ExecutionTestUtil.Recorder(log));

            // Free Join covers with f, which holds both variables, and probes q for x.
            var rsb = bottom.Database.NewRuleSet();
            var b = rsb.NewRule();
            b.Strategy = PlanStrategy.MinCover;
            b.NoDecomposition = true;
            var x = b.NewVariable("x");
            var y = b.NewVariable("y");
            b.AddAtom(f, [x, y, QueryEntry.Wildcard()]);
            b.AddAtom(q, [x, QueryEntry.Wildcard()]);
            var rb = b.Build();
            rb.CallExternal(recorder, [x, y]);
            var rule = rb.Build("f and q");
            CachedPlan cached;
            using (var rs = rsb.Build())
                cached = rs.BuildCachedPlan(rule);

            var report = LayeredRuleUtil.Run(bottom, rsb => rsb.AddRuleFromCachedPlan(cached));
            var expected = visible.Where(r => marked.Contains(uint.Parse(r.Split(',')[0]))).OrderBy(s => s, StringComparer.Ordinal);
            Assert.Equal(expected, ExecutionTestUtil.Distinct(log));
            Assert.Equal(3, report.NumMatches("f and q"));
            Dispose(chain);
        }

    }

}

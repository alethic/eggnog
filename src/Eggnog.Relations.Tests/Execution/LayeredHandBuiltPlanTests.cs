using System;
using System.Collections.Generic;
using System.Linq;

using Xunit;

using static Eggnog.Relations.Tests.LayeredTestUtil;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// The layered kernels the planner never emits a stage for (colored-contexts spec §9.5): a one-scan intersection
    /// (<c>RunIntersectOne</c>) and a probe-free Free Join cover that a later stage scans again (the chunked, non-leaf
    /// <c>RunFusedScan</c>). Generic Join compiles a single-occurrence variable to a fused scan and merges fused scans of
    /// one atom, and the stage ordering never puts a probe-free cover before a stage reading its atom, so no planned rule
    /// reaches them; a cached plan built by hand does. Both run in a child whose view of <c>f</c> has two layers and hides
    /// the root's row of a key the child rewrote.
    /// </summary>
    public class LayeredHandBuiltPlanTests
    {

        /// <summary>
        /// A child whose view of <c>f(k) → v</c> holds the root's <c>f(a) = 1</c> (hidden: the child wrote
        /// <c>f(a) = 5</c>), the root's <c>f(b) = 2</c> and <c>f(c) = 3</c>, and the child's <c>f(a) = 5</c> and
        /// <c>f(d) = 4</c>; the rows the child sees, as "k,v".
        /// </summary>
        static (Store Store, TableId F, Context Child, string[] Visible) Setup()
        {
            var store = new Store(ParallelHeuristics.Serial);
            var f = store.AddTable(MaxFunction(1), name: "f");
            var root = store.Root;
            var a = root.NewId();
            var b = root.NewId();
            var c = root.NewId();
            var d = root.NewId();
            root.Write(state =>
            {
                state.StageInsert(f, [a, 1, state.Timestamp]);
                state.StageInsert(f, [b, 2, state.Timestamp]);
                state.StageInsert(f, [c, 3, state.Timestamp]);
                return 0;
            });

            root.Freeze();
            var child = root.CreateChild();
            child.Write(state =>
            {
                state.StageInsert(f, [a, 5, state.Timestamp]);
                state.StageInsert(f, [d, 4, state.Timestamp]);
                return 0;
            });

            Assert.Equal(2, child.GetView(f).Layers.Count(l => l is not null));
            return (store, f, child, [$"{a},5", $"{b},2", $"{c},3", $"{d},4"]);
        }

        /// <summary>
        /// Plans <c>f(x, y) → record(x, y)</c> in <paramref name="child"/> and returns the plan with its stages replaced by
        /// <paramref name="stages"/>, as a cached plan.
        /// </summary>
        static CachedPlan HandBuilt(Store store, TableId f, Context child, ExternalFunctionId recorder, bool bindY, Func<AtomId, Variable, Variable, JoinStage[]> stages)
        {
            var rsb = child.Database.NewRuleSet();
            var q = rsb.NewRule();
            var x = q.NewVariable("x");
            var y = q.NewVariable("y");
            q.AddAtom(f, [x, bindY ? y : QueryEntry.Wildcard(), QueryEntry.Wildcard()]);
            var rb = q.Build();
            rb.CallExternal(recorder, bindY ? [x, y] : [x]);
            var rule = rb.Build("hand-built");
            using var rs = rsb.Build();
            var planned = rs[rule];
            var template = planned.Template;
            var atom = new AtomId(0);
            Assert.True(template.Atoms.TryGetValue(atom, out _));
            var plan = new SinglePlan(template.Atoms, template.VariableCount, template.Headers, template.ExistenceChecks, stages(atom, x, y), template.Action);
            return new CachedPlan(plan, "hand-built", planned.Symbols, rs.GetAction(planned.Action), planned.IdConstants, []);
        }

        [Fact]
        public void AOneScanIntersectionEnumeratesEveryLayer()
        {
            var (store, f, child, visible) = Setup();
            using (child)
            {
                var log = new List<uint[]>();
                var recorder = store.Root.Database.AddExternalFunction(ExecutionTestUtil.Recorder(log));
                var cached = HandBuilt(store, f, child, recorder, bindY: false, (atom, x, _) => [new IntersectStage(x, [new SingleScanSpec(atom, new ColumnId(0), [])])]);
                var report = LayeredRuleUtil.Run(child, rsb => rsb.AddRuleFromCachedPlan(cached));
                Assert.Equal(visible.Select(r => r.Split(',')[0]).OrderBy(s => s, StringComparer.Ordinal), ExecutionTestUtil.Distinct(log));
                Assert.Equal(4, report.NumMatches("hand-built"));
            }
        }

        [Fact]
        public void AChunkedCoverScanSeesWhatTheViewShows()
        {
            var (store, f, child, visible) = Setup();
            using (child)
            {
                var log = new List<uint[]>();
                var recorder = store.Root.Database.AddExternalFunction(ExecutionTestUtil.Recorder(log));

                // Two probe-free covers of f: the first binds x and is not a leaf (the second scans f again), so it
                // scans f in chunks, one frame per row, refining f to that row of its layer.
                var cached = HandBuilt(store, f, child, recorder, bindY: true, (atom, x, y) =>
                [
                    new FusedIntersectStage(new ScanSpec(new SubAtom(atom, [new ColumnId(0)]), []), [new StageBinding(new ColumnId(0), x)], []),
                    new FusedIntersectStage(new ScanSpec(new SubAtom(atom, [new ColumnId(1)]), []), [new StageBinding(new ColumnId(1), y)], []),
                ]);
                var report = LayeredRuleUtil.Run(child, rsb => rsb.AddRuleFromCachedPlan(cached));
                Assert.Equal(visible.OrderBy(s => s, StringComparer.Ordinal), ExecutionTestUtil.Distinct(log));
                Assert.Equal(4, report.NumMatches("hand-built"));
            }
        }

    }

}

using System;
using System.Collections.Generic;
using System.Linq;

using Xunit;

using static Eggnog.Relations.Tests.LayeredTestUtil;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// The root twin of <see cref="LayeredHandBuiltPlanTests"/>' chunked cover: a probe-free Free Join cover that a later
    /// stage scans again (the chunked, non-leaf <c>RunFusedScan</c>), run at the root, whose views are plain, over more
    /// rows than one morsel. The planner never emits a probe-free cover before a stage reading its atom, so cached plans
    /// built by hand reach this path. With the executor's counters on, each run also checks that a root scans only its
    /// own rows, each once.
    /// </summary>
    public class RootHandBuiltPlanTests
    {

        /// <summary>
        /// The rows of <c>f</c>: more than <see cref="JoinRun.DefaultMorselSize"/>, so a cover scan takes several chunks.
        /// </summary>
        const int N = 600;

        /// <summary>
        /// A root holding <c>f(k_i) = i + 1</c> for <see cref="N"/> fresh ids <c>k_i</c>; its ids and the rows as "k,v".
        /// </summary>
        static (Store Store, TableId F, uint[] Ids, string[] Rows) Setup()
        {
            var store = new Store(ParallelHeuristics.Serial);
            var f = store.AddTable(MaxFunction(1), name: "f");
            var root = store.Root;
            var ids = Enumerable.Range(0, N).Select(_ => root.NewId()).ToArray();
            root.Write(state =>
            {
                for (var i = 0; i < N; i++)
                    state.StageInsert(f, [ids[i], (uint)(i + 1), state.Timestamp]);

                return 0;
            });

            return (store, f, ids, ids.Select((id, i) => $"{id},{i + 1}").ToArray());
        }

        /// <summary>
        /// Returns rule <paramref name="rule"/> of <paramref name="rs"/> as a cached plan whose stages are
        /// <paramref name="stages"/> in place of the planner's.
        /// </summary>
        static CachedPlan WithStages(RuleSet rs, RuleId rule, string name, JoinStage[] stages)
        {
            var planned = rs[rule];
            var template = planned.Template;
            var plan = new SinglePlan(template.Atoms, template.VariableCount, template.Headers, template.ExistenceChecks, stages, template.Action);
            return new CachedPlan(plan, name, planned.Symbols, rs.GetAction(planned.Action), planned.IdConstants, []);
        }

        /// <summary>
        /// A probe-free cover of the column <paramref name="column"/> of <paramref name="atom"/> that binds it to
        /// <paramref name="variable"/>, with <paramref name="probes"/>.
        /// </summary>
        static FusedIntersectStage Cover(AtomId atom, int column, Variable variable, params Probe[] probes) =>
            new(new ScanSpec(new SubAtom(atom, [new ColumnId(column)]), []), [new StageBinding(new ColumnId(column), variable)], probes);

        /// <summary>
        /// Asserts that the run scanned <paramref name="own"/> rows of the root and none of an ancestor, when the
        /// executor's counters are on.
        /// </summary>
        static void AssertOwnRowsOnly(ExecutorCounters? counts, long own)
        {
            if (counts is null)
                return;

            Assert.Equal(0, counts.AncestorRowsScanned);
            Assert.Equal(0, counts.AncestorRefinedRowsScanned);
            Assert.Equal(own, counts.OwnRowsScanned);
        }

        /// <summary>
        /// <c>f(x, y)</c> as two covers of <c>f</c>: the first binds <c>x</c> and is not a leaf, so it scans <c>f</c> in
        /// chunks, one frame per row, refining <c>f</c> to that row; the second scans that row. Every row comes out
        /// once, and each cover scans every row once.
        /// </summary>
        [Fact]
        public void AChunkedCoverScanRefinesItsAtomToEachRow()
        {
            var (store, f, _, rows) = Setup();
            var db = store.Root.Database;
            var log = new List<uint[]>();
            var recorder = db.AddExternalFunction(ExecutionTestUtil.Recorder(log));
            var rsb = db.NewRuleSet();
            var q = rsb.NewRule();
            var x = q.NewVariable("x");
            var y = q.NewVariable("y");
            q.AddAtom(f, [x, y, QueryEntry.Wildcard()]);
            var rb = q.Build();
            rb.CallExternal(recorder, [x, y]);
            var rule = rb.Build("hand-built");
            using var rs = rsb.Build();
            var fa = new AtomId(0);
            var cached = WithStages(rs, rule, "hand-built", [Cover(fa, 0, x), Cover(fa, 1, y)]);

            var counts = db.ExecutorCounts;
            counts?.Reset();
            var report = LayeredRuleUtil.Run(store.Root, r => r.AddRuleFromCachedPlan(cached));
            Assert.Equal(rows.OrderBy(s => s, StringComparer.Ordinal), ExecutionTestUtil.Distinct(log));
            Assert.Equal(N, log.Count);
            Assert.Equal(N, report.NumMatches("hand-built"));
            AssertOwnRowsOnly(counts, 2 * N);
        }

        /// <summary>
        /// <c>f(x, y), h(z, w)</c> with the chunked cover of <c>f</c> nested inside a cover of <c>h</c>: the inner scan
        /// gives <c>f</c> back whole after each outer row, so every row of <c>h</c> meets every row of <c>f</c>.
        /// </summary>
        [Fact]
        public void AnInnerChunkedCoverRunsOncePerOuterRow()
        {
            var (store, f, _, _) = Setup();
            var h = store.AddTable(Relation(2), name: "h");
            var hs = new uint[3];
            store.Root.Write(state =>
            {
                for (var i = 0; i < hs.Length; i++)
                {
                    hs[i] = (uint)(100_000 + i);
                    state.StageInsert(h, [hs[i], hs[i] + 7, state.Timestamp]);
                }

                return 0;
            });

            var db = store.Root.Database;
            var log = new List<uint[]>();
            var recorder = db.AddExternalFunction(ExecutionTestUtil.Recorder(log));
            var rsb = db.NewRuleSet();
            var q = rsb.NewRule();
            var x = q.NewVariable("x");
            var y = q.NewVariable("y");
            var z = q.NewVariable("z");
            var w = q.NewVariable("w");
            q.AddAtom(f, [x, y, QueryEntry.Wildcard()]);
            q.AddAtom(h, [z, w, QueryEntry.Wildcard()]);
            var rb = q.Build();
            rb.CallExternal(recorder, [x, y, z, w]);
            var rule = rb.Build("cross");
            using var rs = rsb.Build();
            var fa = new AtomId(0);
            var ha = new AtomId(1);
            var cached = WithStages(rs, rule, "cross", [Cover(ha, 0, z), Cover(fa, 0, x), Cover(fa, 1, y), Cover(ha, 1, w)]);

            LayeredRuleUtil.Run(store.Root, r => r.AddRuleFromCachedPlan(cached));
            Assert.Equal(N * hs.Length, log.Count);
            Assert.Equal(N * hs.Length, ExecutionTestUtil.Distinct(log).Count);
        }

    }

}

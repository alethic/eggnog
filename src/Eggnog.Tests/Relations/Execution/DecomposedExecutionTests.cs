using System;
using System.Collections.Generic;
using System.Linq;

using Xunit;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// Execution of decomposed plans (materializations, <see cref="MatScanMode.KeyOnly"/> prologues,
    /// <see cref="MatScanMode.Full"/> and <see cref="MatScanMode.Value"/> result scans, <see cref="MatScanMode.Lookup"/>
    /// semijoins, materialization leaf scans) on the planner experiments' queries over a real database, in every
    /// executor mode, against a brute-force reference.
    /// </summary>
    [Collection(ExecutorStressCollection.Name)]
    public class DecomposedExecutionTests
    {

        public static IEnumerable<object[]> Cases =>
            from name in DecomposedQueries.Names
            from strategy in new[] { "Gj", "PureSize", "MinCover" }
            from mode in ExecutionTestUtil.Modes
            select new object[] { name, strategy, mode };

        [Theory]
        [MemberData(nameof(Cases))]
        public void ExperimentsMatchTheReference(string name, string strategyName, string mode)
        {
            var strategy = Enum.Parse<PlanStrategy>(strategyName);
            foreach (var noDecomposition in new[] { false, true })
            {
                var log = new List<uint[]>();
                var (db, rs) = DecomposedQueries.Build(name, strategy, mode, log, noDecomposition);
                using (rs)
                {
                    var expected = DecomposedQueries.Reference(db, name);
                    var report = db.RunRuleSet(rs);
                    var got = ExecutionTestUtil.Distinct(log);
                    Assert.True(expected.SetEquals(got), $"{name} {strategy} {mode} noDecomposition {noDecomposition}\n{rs[new RuleId(0)].Template}\nexpected {expected.Count}, got {got.Count}");
                    Assert.Equal(log.Count, report.NumMatches(name));

                    // The experiments' data has no cycle, so the cyclic queries have no match.
                    if (name is not ("rect" or "triangle"))
                        Assert.NotEmpty(expected);
                }
            }
        }

        [Fact]
        public void ExperimentsDecompose()
        {
            // The planner decomposes the chain and tree queries on these statistics, so the cases above run every
            // materialization mode but Lookup.
            var modes = new HashSet<MatScanMode>();
            foreach (var name in new[] { "path4", "path6", "rect", "tree5", "spider" })
            {
                var (_, rs) = DecomposedQueries.Build(name, PlanStrategy.PureSize, "Serial", new List<uint[]>());
                using (rs)
                {
                    var plan = Assert.IsType<DecomposedPlan>(rs[new RuleId(0)].Template);
                    foreach (var stage in plan.Blocks.SelectMany(b => b.Stages).Concat(plan.ResultBlock).OfType<FusedIntersectMatStage>())
                        modes.Add(stage.Mode);
                }
            }

            Assert.Equal(new[] { MatScanMode.Full, MatScanMode.KeyOnly, MatScanMode.Value }, modes.OrderBy(m => m));
        }

        /// <summary>
        /// A <see cref="MatScanMode.Lookup"/> stage is a semijoin: appended to path4's result block with the first
        /// block's materialization looked up by <c>v0</c>, it keeps exactly the matches whose <c>v0</c> is a key of that
        /// materialization.
        /// </summary>
        [Theory]
        [InlineData("Serial")]
        [InlineData("Parallel1")]
        [InlineData("Parallel4")]
        public void LookupIsASemijoin(string mode)
        {
            var log = new List<uint[]>();
            var (db, built) = DecomposedQueries.Build("path4", PlanStrategy.PureSize, mode, log);
            using (built)
            {
                var original = built[new RuleId(0)];
                var plan = Assert.IsType<DecomposedPlan>(original.Template);
                Assert.Equal("msg[v2] val[v4]", plan.Blocks[0].Spec.ToString());
                var v0 = new Variable(0);
                var lookup = new FusedIntersectMatStage(new MatId(0), MatScanMode.Lookup, [v0], [], []);
                var template = new DecomposedPlan(plan.Atoms, plan.VariableCount, plan.Headers, plan.ExistenceChecks, plan.Blocks, [.. plan.ResultBlock, lookup], plan.Action);
                using var rs = Rebuild(db, built, original, template);
                db.RunRuleSet(rs);

                // The keys of materialization 0: the v2 of a1(v1, v2), a2(v2, v3), a3(v3, v4).
                var rows = Enumerable.Range(0, 4).Select(t => TableTestUtil.LiveRows(db.GetTable(new TableId(t)))).ToArray();
                var keys = (from r1 in rows[1] from r2 in rows[2] where r1[1] == r2[0] from r3 in rows[3] where r2[1] == r3[0] select r1[1]).ToHashSet();
                var expected = new SortedSet<string>(DecomposedQueries.Reference(db, "path4").Where(t => keys.Contains(uint.Parse(t.Split(',')[0]))), StringComparer.Ordinal);
                Assert.NotEmpty(expected);
                Assert.True(expected.Count < DecomposedQueries.Reference(db, "path4").Count, "the lookup should filter some matches");
                Assert.True(expected.SetEquals(ExecutionTestUtil.Distinct(log)));
            }
        }

        /// <summary>
        /// An early stop requested by the action ends a decomposed join: the serial run stops at the first check after
        /// the first batch of matches, so it records fewer matches than a run without the stop, each of them a match of
        /// the query.
        /// </summary>
        [Theory]
        [InlineData("tree5")]
        [InlineData("spider")]
        public void AnEarlyStopEndsADecomposedJoin(string name)
        {
            var log = new List<uint[]>();
            var stopper = ExternalFunction.Create((ExecutionState state, ReadOnlySpan<uint> args, out uint result) =>
            {
                log.Add(args.ToArray());
                state.TriggerEarlyStop();
                result = 0;
                return true;
            });
            var (db, rs) = DecomposedQueries.Build(name, PlanStrategy.PureSize, "Serial", log, action: stopper);
            using (rs)
            {
                Assert.IsType<DecomposedPlan>(rs[new RuleId(0)].Template);
                var reference = DecomposedQueries.Reference(db, name);

                db.RunRuleSet(rs);

                Assert.NotEmpty(log);
                Assert.True(log.Count < FullMatchCount(name), $"{name}: {log.Count} matches recorded before the stop");
                Assert.All(log, match => Assert.Contains(string.Join(",", match), reference));
            }
        }

        /// <summary>
        /// The number of matches a serial run of an experiment records without a stop.
        /// </summary>
        static int FullMatchCount(string name)
        {
            var log = new List<uint[]>();
            var (db, rs) = DecomposedQueries.Build(name, PlanStrategy.PureSize, "Serial", log);
            using (rs)
                db.RunRuleSet(rs);

            return log.Count;
        }

        static RuleSet Rebuild(Database db, RuleSet built, RulePlan original, PlanTemplate template)
        {
            var evaluator = db;
            var instance = template.Instantiate(ref evaluator, default, original.Action);
            var rules = new IdVec<RuleId, RulePlan>();
            rules.Push(new RulePlan(template, original.Action, instance, original.Description, original.Symbols, original.IdConstants, original.ExtraConstraints));
            var actions = new DenseIdMap<ActionId, ActionInfo>();
            actions.Push(built.GetAction(original.Action));
            return new RuleSet(rules, actions);
        }

    }

}

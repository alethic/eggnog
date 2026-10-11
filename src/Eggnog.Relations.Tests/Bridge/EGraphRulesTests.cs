using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

using Xunit;

using static Eggnog.Relations.Tests.BridgeTestUtil;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// Tests for running e-graph rules (egglog-bridge/src/lib.rs:583-624, 1397-1420; rule.rs:773-846): rule ids and
    /// watermarks, plans built once, the semi-naive variants, naive rules, match counts, the iteration report, and what
    /// a failing run leaves behind.
    /// </summary>
    public class EGraphRulesTests
    {

        /// <summary>
        /// A rule over the edge relation <c>edge(x, y)</c> that copies every edge into <c>seen</c>.
        /// </summary>
        static EGraphRuleId CopyRule(EGraph egraph, FunctionId edge, FunctionId seen, bool semiNaive, string description = "copy")
        {
            var rb = egraph.NewRule(description, semiNaive);
            var x = rb.NewVar(ColumnTy.Id);
            var y = rb.NewVar(ColumnTy.Id);
            var o = rb.NewVar(ColumnTy.Id);
            rb.QueryTable(edge, [x, y, o], false);
            rb.Set(seen, [x, y, o]);
            return rb.Build();
        }

        /// <summary>
        /// Two relation-like constructors over pairs of ids, and <paramref name="count"/> fresh ids.
        /// </summary>
        static (EGraph EGraph, FunctionId Edge, FunctionId Seen, Value[] Ids) Graph(int count)
        {
            var egraph = NewEGraph();
            var edge = Constructor(egraph, "edge", false, ColumnTy.Id, ColumnTy.Id);
            var seen = Constructor(egraph, "seen", false, ColumnTy.Id, ColumnTy.Id);
            var ids = Enumerable.Range(0, count).Select(_ => egraph.FreshId()).ToArray();
            return (egraph, edge, seen, ids);
        }

        [Fact]
        public void RuleIdsAreReservedInOrderAndReusedAfterAFree()
        {
            var (egraph, edge, seen, _) = Graph(0);
            var r0 = CopyRule(egraph, edge, seen, true);
            var r1 = CopyRule(egraph, edge, seen, true);
            Assert.Equal(new EGraphRuleId(0), r0);
            Assert.Equal(new EGraphRuleId(1), r1);
            Assert.Equal(2, egraph.RuleCount);

            egraph.FreeRule(r0);
            Assert.Equal(1, egraph.RuleCount);
            Assert.Throws<ArgumentException>(() => egraph.RunRules(r0));
            egraph.FreeRule(r0);
            egraph.FreeRule(new EGraphRuleId(17));

            Assert.Equal(r0, CopyRule(egraph, edge, seen, true));
            var pending = egraph.NewRule("never built", true);
            Assert.Equal(new EGraphRuleId(2), pending.RuleId);
            Assert.Throws<ArgumentException>(() => egraph.RunRules(pending.RuleId));
            Assert.Equal(new EGraphRuleId(3), CopyRule(egraph, edge, seen, true));
        }

        [Fact]
        public void FreeingARuleFreesItsLazyPanicsButNotInternedOnes()
        {
            var fx = new RunFixture();
            var egraph = fx.EGraph;
            var partial = egraph.AddTable(new FunctionConfig
            {
                Schema = [ColumnTy.Id, ColumnTy.Id],
                Default = DefaultVal.Fail,
                Merge = MergeFn.AssertEq,
                Name = "partial",
            });
            var identity = Register(egraph, (_, args) => args[0]);
            var before = egraph.Database.ExternalFunctions.Count;

            var rb = egraph.NewRule("owns panics", true);
            var x = rb.NewVar(ColumnTy.Id);
            var y = rb.NewVar(ColumnTy.Id);
            var o = rb.NewVar(ColumnTy.Id);
            rb.QueryTable(fx.Same, [x, y, o], false);
            rb.Lookup(partial, [x], () => "missing");
            rb.CallExternalFunc(identity, [y], ColumnTy.Id, () => "failed");
            rb.Panic("interned");
            var rule = rb.Build();

            var owned = egraph.GetRuleInfo(rule).Query.OwnedFunctions.ToArray();
            Assert.Equal(2, owned.Length);
            Assert.Equal(before + 3, egraph.Database.ExternalFunctions.Count);

            egraph.FreeRule(rule);
            Assert.Equal(before + 1, egraph.Database.ExternalFunctions.Count);
            Assert.Equal(egraph.NewPanic("interned"), egraph.NewPanic("interned"));
        }

        [Fact]
        public void RulesArePlannedOnceAtTheirFirstRun()
        {
            var (egraph, edge, seen, ids) = Graph(2);
            var rule = CopyRule(egraph, edge, seen, true);
            Assert.Null(egraph.GetRuleInfo(rule).CachedPlan);

            egraph.RunRules(rule);
            var plan = egraph.GetRuleInfo(rule).CachedPlan;
            Assert.NotNull(plan);
            Assert.Equal(1, plan!.AtomMapping.Length);

            // Planned against empty tables; still correct, and never planned again.
            egraph.AddValues([(edge, [ids[0], ids[1], ids[0]])]);
            var report = egraph.RunRules(rule);
            Assert.Same(plan, egraph.GetRuleInfo(rule).CachedPlan);
            Assert.Equal(1, report.RuleSetReport.NumMatches("copy"));
            Assert.Single(Rows(egraph, seen));
        }

        [Fact]
        public void ARunRecordsItsTimestampAsTheWatermarkOfTheRulesItRan()
        {
            var (egraph, edge, seen, _) = Graph(0);
            var a = CopyRule(egraph, edge, seen, true, "a");
            var b = CopyRule(egraph, edge, seen, true, "b");
            Assert.Equal(0u, egraph.GetRuleInfo(a).LastRunAt);

            var t1 = egraph.NextTimestamp;
            egraph.RunRules(a);
            Assert.Equal(t1, egraph.GetRuleInfo(a).LastRunAt);
            Assert.Equal(0u, egraph.GetRuleInfo(b).LastRunAt);
            Assert.Equal(t1 + 1, egraph.NextTimestamp);

            var t2 = egraph.NextTimestamp;
            egraph.RunRules(a, b);
            Assert.Equal(t2, egraph.GetRuleInfo(a).LastRunAt);
            Assert.Equal(t2, egraph.GetRuleInfo(b).LastRunAt);
        }

        /// <summary>
        /// The variants of egglog's <c>add_rules_from_cached</c>, inspected in the rule set they are added to.
        /// </summary>
        [Fact]
        public void SemiNaiveVariantsFollowEgglog()
        {
            var (egraph, edge, _, ids) = Graph(4);

            // Rows at timestamp 1 and at timestamp 2.
            egraph.AddValues([(edge, [ids[0], ids[1], ids[0]]), (edge, [ids[1], ids[2], ids[0]])]);
            egraph.AddValues([(edge, [ids[2], ids[3], ids[0]])]);
            Assert.Equal(3u, egraph.NextTimestamp);

            var rb = egraph.NewRule("path3", true);
            var v = Enumerable.Range(0, 7).Select(_ => rb.NewVar(ColumnTy.Id)).ToArray();
            rb.QueryTable(edge, [v[0], v[1], v[4]], false);
            rb.QueryTable(edge, [v[1], v[2], v[5]], false);
            rb.QueryTable(edge, [v[2], v[3], v[6]], false);
            var rule = rb.Build();
            var info = egraph.GetRuleInfo(rule);
            var plan = info.Query.BuildCachedPlan(egraph.Database, info.Description);
            var map = plan.AtomMapping.ToArray();
            var ts = new ColumnId(egraph.GetFunctionInfo(edge).Math.TimestampColumn);

            (AtomId, Constraint)[][] Variants(BridgeQuery query, uint mid, CachedPlanInfo cached)
            {
                var rsb = new RuleSetBuilder(egraph.Database);
                query.AddRulesFromCached(rsb, mid, cached);
                using var set = rsb.Build();
                return set.Rules.ToArray().Select(r => r.ExtraConstraints.ToArray()).ToArray();
            }

            // First run: every row is new and none is old, so only the first variant.
            var first = Variants(info.Query, 0, plan);
            Assert.Single(first);
            Assert.Equal([(map[0], Constraint.GeConst(ts, new Value(0)))], first[0]);

            // Later: A_new x B x C + A_old x B_new x C + A_old x B_old x C_new.
            var mid = Variants(info.Query, 2, plan);
            Assert.Equal(3, mid.Length);
            Assert.Equal([(map[0], Constraint.GeConst(ts, new Value(2)))], mid[0]);
            Assert.Equal([(map[1], Constraint.GeConst(ts, new Value(2))), (map[0], Constraint.LtConst(ts, new Value(2)))], mid[1]);
            Assert.Equal([(map[2], Constraint.GeConst(ts, new Value(2))), (map[0], Constraint.LtConst(ts, new Value(2))), (map[1], Constraint.LtConst(ts, new Value(2)))], mid[2]);

            // Nothing new: every variant is dropped before any work.
            Assert.Empty(Variants(info.Query, 3, plan));

            // A naive rule is added as planned, every time.
            var naive = egraph.NewRule("naive", false);
            var n = Enumerable.Range(0, 3).Select(_ => naive.NewVar(ColumnTy.Id)).ToArray();
            naive.QueryTable(edge, [n[0], n[1], n[2]], false);
            var naiveInfo = egraph.GetRuleInfo(naive.Build());
            var naivePlan = naiveInfo.Query.BuildCachedPlan(egraph.Database, naiveInfo.Description);
            Assert.Equal([Array.Empty<(AtomId, Constraint)>()], Variants(naiveInfo.Query, 3, naivePlan));

            // A semi-naive rule without atoms: once in its life.
            var rhsOnly = egraph.NewRule("rhs only", true);
            rhsOnly.Union(RuleEntry.Const(ids[0], ColumnTy.Id), RuleEntry.Const(ids[0], ColumnTy.Id));
            var rhsInfo = egraph.GetRuleInfo(rhsOnly.Build());
            var rhsPlan = rhsInfo.Query.BuildCachedPlan(egraph.Database, rhsInfo.Description);
            Assert.Single(Variants(rhsInfo.Query, 0, rhsPlan));
            Assert.Empty(Variants(rhsInfo.Query, 1, rhsPlan));

            // A sole focus: one variant, on that atom.
            var focused = egraph.NewRule("focused", true);
            var f = Enumerable.Range(0, 5).Select(_ => focused.NewVar(ColumnTy.Id)).ToArray();
            focused.QueryTable(edge, [f[0], f[1], f[3]], false);
            focused.QueryTable(edge, [f[1], f[2], f[4]], false);
            focused.SetFocus(1);
            var focusedInfo = egraph.GetRuleInfo(focused.Build());
            var focusedPlan = focusedInfo.Query.BuildCachedPlan(egraph.Database, focusedInfo.Description);
            var focusedVariants = Variants(focusedInfo.Query, 2, focusedPlan);
            Assert.Single(focusedVariants);
            Assert.Equal([(focusedPlan.AtomMapping[1], Constraint.GeConst(ts, new Value(2)))], focusedVariants[0]);
        }

        [Fact]
        public void SemiNaiveRunsMatchOnlyNewRowsAndNaiveRunsMatchEverything()
        {
            var (egraph, edge, seen, ids) = Graph(5);
            var semi = CopyRule(egraph, edge, seen, true, "semi");
            var naive = CopyRule(egraph, edge, seen, false, "naive");
            egraph.AddValues([(edge, [ids[0], ids[1], ids[4]]), (edge, [ids[1], ids[2], ids[4]]), (edge, [ids[2], ids[3], ids[4]])]);

            var report = egraph.RunRules(semi, naive);
            Assert.True(report.Changed);
            Assert.Equal(3, report.RuleSetReport.NumMatches("semi"));
            Assert.Equal(3, report.RuleSetReport.NumMatches("naive"));

            egraph.AddValues([(edge, [ids[3], ids[0], ids[4]])]);
            report = egraph.RunRules(semi, naive);
            Assert.Equal(1, report.RuleSetReport.NumMatches("semi"));
            Assert.Equal(4, report.RuleSetReport.NumMatches("naive"));
            Assert.True(report.Changed);

            report = egraph.RunRules(semi, naive);
            Assert.Equal(0, report.RuleSetReport.NumMatches("semi"));
            Assert.Equal(4, report.RuleSetReport.NumMatches("naive"));
            Assert.False(report.Changed);
            Assert.Equal(4, egraph.TableSize(seen));
        }

        [Fact]
        public void MatchCountsSumOverRulesSharingADescription()
        {
            var (egraph, edge, seen, ids) = Graph(3);
            var a = CopyRule(egraph, edge, seen, true, "shared");
            var b = CopyRule(egraph, edge, seen, false, "shared");
            egraph.AddValues([(edge, [ids[0], ids[1], ids[2]]), (edge, [ids[1], ids[2], ids[2]])]);
            var report = egraph.RunRules(a, b);
            Assert.Equal(4, report.RuleSetReport.NumMatches("shared"));
            Assert.Equal(2, report.RuleSetReport.RuleReports["shared"].Count);
            Assert.Equal(0, report.RuleSetReport.NumMatches("absent"));
        }

        /// <summary>
        /// The transitive closure of a chain, semi-naively and naively: the same paths after every iteration, in the same
        /// number of iterations, with fewer matches semi-naively.
        /// </summary>
        [Fact]
        public void TransitiveClosureIsTheSameSemiNaivelyAndNaively()
        {
            const int N = 12;
            (EGraph, FunctionId, EGraphRuleId[]) Build(bool semiNaive)
            {
                var egraph = NewEGraph();
                var edge = Constructor(egraph, "edge", false, ColumnTy.Id, ColumnTy.Id);
                var path = Constructor(egraph, "path", false, ColumnTy.Id, ColumnTy.Id);
                var ids = Enumerable.Range(0, N + 1).Select(_ => egraph.FreshId()).ToArray();
                egraph.AddValues(Enumerable.Range(0, N).Select(i => (edge, new[] { ids[i], ids[i + 1], ids[0] })));

                var baseCase = egraph.NewRule("base", semiNaive);
                var bx = baseCase.NewVar(ColumnTy.Id);
                var by = baseCase.NewVar(ColumnTy.Id);
                var bo = baseCase.NewVar(ColumnTy.Id);
                baseCase.QueryTable(edge, [bx, by, bo], false);
                baseCase.Set(path, [bx, by, bo]);

                var step = egraph.NewRule("step", semiNaive);
                var x = step.NewVar(ColumnTy.Id);
                var y = step.NewVar(ColumnTy.Id);
                var z = step.NewVar(ColumnTy.Id);
                var o1 = step.NewVar(ColumnTy.Id);
                var o2 = step.NewVar(ColumnTy.Id);
                step.QueryTable(path, [x, y, o1], false);
                step.QueryTable(edge, [y, z, o2], false);
                step.Set(path, [x, z, o1]);
                return (egraph, path, [baseCase.Build(), step.Build()]);
            }

            var (semiGraph, semiPath, semiRules) = Build(true);
            var (naiveGraph, naivePath, naiveRules) = Build(false);
            long semiMatches = 0, naiveMatches = 0;
            var iterations = 0;
            while (true)
            {
                var semiReport = semiGraph.RunRules(semiRules);
                var naiveReport = naiveGraph.RunRules(naiveRules);
                Assert.Equal(semiReport.Changed, naiveReport.Changed);
                Assert.Equal(Show(naiveGraph, naivePath), Show(semiGraph, semiPath));
                semiMatches += semiReport.RuleSetReport.NumMatches("base") + semiReport.RuleSetReport.NumMatches("step");
                naiveMatches += naiveReport.RuleSetReport.NumMatches("base") + naiveReport.RuleSetReport.NumMatches("step");
                iterations++;
                if (semiReport.Changed == false)
                    break;
            }

            Assert.Equal(N * (N + 1) / 2, semiGraph.TableSize(semiPath));
            Assert.Equal(N + 1, iterations);
            Assert.True(semiMatches < naiveMatches, $"{semiMatches} semi-naive matches, {naiveMatches} naive");
        }

        [Fact]
        public void WritesOfARunAreNewToTheNextRunOnly()
        {
            var (egraph, edge, seen, ids) = Graph(3);
            var copy = CopyRule(egraph, edge, seen, true, "copy");

            // Reads seen, which copy fills: not in the same run (every rule matches the same snapshot).
            var echo = egraph.NewRule("echo", true);
            var x = echo.NewVar(ColumnTy.Id);
            var y = echo.NewVar(ColumnTy.Id);
            var o = echo.NewVar(ColumnTy.Id);
            echo.QueryTable(seen, [x, y, o], false);
            echo.Set(edge, [y, x, o]);
            var echoRule = echo.Build();

            egraph.AddValues([(edge, [ids[0], ids[1], ids[2]])]);
            var t = egraph.NextTimestamp;
            var report = egraph.RunRules(copy, echoRule);
            Assert.Equal(0, report.RuleSetReport.NumMatches("echo"));
            Assert.Equal(t, PhysicalRow(egraph, seen, ids[0].Rep, ids[1].Rep)[3]);

            report = egraph.RunRules(copy, echoRule);
            Assert.Equal(1, report.RuleSetReport.NumMatches("echo"));
            Assert.Equal(0, report.RuleSetReport.NumMatches("copy"));
            Assert.Equal(2, egraph.TableSize(edge));
        }

        [Fact]
        public void RowsARebuildRewritesAreNewToTheNextRun()
        {
            var (egraph, edge, seen, ids) = Graph(4);
            var copy = CopyRule(egraph, edge, seen, true, "copy");
            egraph.AddValues([(edge, [ids[2], ids[1], ids[3]]), (edge, [ids[1], ids[3], ids[3]])]);
            Assert.Equal(2, egraph.RunRules(copy).RuleSetReport.NumMatches("copy"));
            Assert.Equal(0, egraph.RunRules(copy).RuleSetReport.NumMatches("copy"));

            // The union rewrites edge(2, 1) into edge(0, 1), stamped after the rule's last run, so the rule sees it.
            Union(egraph, ids[0], ids[2]);
            Assert.Equal(2, egraph.TableSize(edge));
            var report = egraph.RunRules(copy);
            Assert.Equal(1, report.RuleSetReport.NumMatches("copy"));
            Assert.Equal(2, egraph.TableSize(seen));

        }

        [Fact]
        public void ARewrittenRowThatMergesIntoAnEqualRowIsNotNew()
        {
            var (egraph, edge, seen, ids) = Graph(3);
            var copy = CopyRule(egraph, edge, seen, true, "copy");
            egraph.AddValues([(edge, [ids[0], ids[2], ids[2]]), (edge, [ids[1], ids[2], ids[2]])]);
            Assert.Equal(2, egraph.RunRules(copy).RuleSetReport.NumMatches("copy"));
            var stamp = PhysicalRow(egraph, edge, ids[0].Rep, ids[2].Rep)[3];

            // edge(1, 2) becomes edge(0, 2), which exists with the same output: the merge changes nothing, so the row
            // keeps its timestamp and the rule has nothing new to see.
            Union(egraph, ids[0], ids[1]);
            Assert.Single(Rows(egraph, edge));
            Assert.Equal(stamp, PhysicalRow(egraph, edge, ids[0].Rep, ids[2].Rep)[3]);
            Assert.Equal(0, egraph.RunRules(copy).RuleSetReport.NumMatches("copy"));
        }

        [Fact]
        public void TheReportSeparatesRuleChangesFromRebuildChanges()
        {
            var fx = new RunFixture();
            var egraph = fx.EGraph;
            var n1 = fx.Number(1);
            var n2 = fx.Number(2);
            egraph.AddTerm(fx.Add, [n1, n1]);
            egraph.AddTerm(fx.Add, [n2, n2]);
            egraph.AddTerm(fx.Same, [n1, n2]);

            var rb = egraph.NewRule("union same", true);
            var x = rb.NewVar(ColumnTy.Id);
            var y = rb.NewVar(ColumnTy.Id);
            var o = rb.NewVar(ColumnTy.Id);
            rb.QueryTable(fx.Same, [x, y, o], false);
            rb.Union(x, y);
            var rule = rb.Build();

            // The union changes the union-find; the rebuild collapses add(1, 1) and add(2, 2).
            var report = egraph.RunRules(rule);
            Assert.True(report.Changed);
            Assert.True(report.RebuildChanged);
            Assert.Single(Rows(egraph, fx.Add));

            // Nothing new: no change, no rebuild, and the timestamp moves by one.
            var before = egraph.NextTimestamp;
            report = egraph.RunRules(rule);
            Assert.False(report.Changed);
            Assert.False(report.RebuildChanged);
            Assert.Equal(TimeSpan.Zero, report.RebuildTime);
            Assert.Equal(before + 1, egraph.NextTimestamp);

            // A union of ids nothing mentions changes the union-find only.
            var a = egraph.FreshId();
            var b = egraph.FreshId();
            var lone = egraph.NewRule("lone union", true);
            lone.Union(RuleEntry.Const(a, ColumnTy.Id), RuleEntry.Const(b, ColumnTy.Id));
            report = egraph.RunRules(lone.Build());
            Assert.True(report.Changed);
            Assert.False(report.RebuildChanged);
            Assert.Equal(a, egraph.GetCanonInUf(b));
        }

        [Fact]
        public void APanicIsThrownAfterTheRunWithTheWatermarkAdvanced()
        {
            var fx = new RunFixture();
            var egraph = fx.EGraph;
            var n1 = fx.Number(1);
            var n2 = fx.Number(2);
            egraph.AddTerm(fx.Same, [n1, n2]);

            var rb = egraph.NewRule("union then panic", true);
            var x = rb.NewVar(ColumnTy.Id);
            var y = rb.NewVar(ColumnTy.Id);
            var o = rb.NewVar(ColumnTy.Id);
            rb.QueryTable(fx.Same, [x, y, o], false);
            rb.Union(x, y);
            rb.Panic("boom");
            var rule = rb.Build();

            var t = egraph.NextTimestamp;
            var error = Assert.Throws<PanicException>(() => egraph.RunRules(rule));
            Assert.Equal("boom", error.PanicMessage);
            Assert.Equal(fx.Canon(n1), fx.Canon(n2));
            Assert.Equal(t, egraph.GetRuleInfo(rule).LastRunAt);
            Assert.True(egraph.NextTimestamp > t);

            // The rebuild re-stamped same(n1, n2) as same(n1, n1): new, so the rule matches and panics again; after
            // that the match is old.
            Assert.Throws<PanicException>(() => egraph.RunRules(rule));
            Assert.False(egraph.RunRules(rule).Changed);
        }

        [Fact]
        public void AnExceptionRestoresTheWatermarks()
        {
            var (egraph, edge, seen, ids) = Graph(3);
            var fail = true;
            var gate = Register(egraph, (_, args) => fail ? throw new InvalidOperationException("gate closed") : args[0]);

            var rb = egraph.NewRule("gated", true);
            var x = rb.NewVar(ColumnTy.Id);
            var y = rb.NewVar(ColumnTy.Id);
            var o = rb.NewVar(ColumnTy.Id);
            rb.QueryTable(edge, [x, y, o], false);
            var passed = rb.CallExternalFunc(gate, [x], ColumnTy.Id, () => "gate");
            rb.Set(seen, [passed, y, o]);
            var rule = rb.Build();
            var other = CopyRule(egraph, edge, seen, true, "other");

            egraph.AddValues([(edge, [ids[0], ids[1], ids[2]])]);
            var t = egraph.NextTimestamp;
            Assert.Throws<InvalidOperationException>(() => egraph.RunRules(other, rule, rule));
            Assert.Equal(0u, egraph.GetRuleInfo(rule).LastRunAt);
            Assert.Equal(0u, egraph.GetRuleInfo(other).LastRunAt);
            Assert.Equal(t, egraph.NextTimestamp);

            fail = false;
            var report = egraph.RunRules(rule);
            Assert.Equal(1, report.RuleSetReport.NumMatches("gated"));
            Assert.Single(Rows(egraph, seen));
        }

        /// <summary>
        /// Registers an i64 addition, for a merge that is not idempotent, so that a match applied twice shows.
        /// </summary>
        static ExternalFunctionId Add(EGraph egraph, Action? onCall = null)
        {
            return Register(egraph, (state, args) =>
            {
                onCall?.Invoke();
                var a = state.BaseValues.Unwrap<Int64Codec, long>(new Value(args[0]));
                var b = state.BaseValues.Unwrap<Int64Codec, long>(new Value(args[1]));
                return state.BaseValues.Get<Int64Codec, long>(a + b).Rep;
            });
        }

        /// <summary>
        /// The constructor <c>src(i64)</c> holding the keys <c>0..count</c>, and the function <c>acc(i64) -&gt; i64</c>
        /// merging by <paramref name="merge"/>.
        /// </summary>
        static (FunctionId Src, FunctionId Acc) CountingTables(EGraph egraph, int count, ExternalFunctionId merge)
        {
            var i64 = IntType(egraph);
            var src = Constructor(egraph, "src", false, i64);
            var acc = egraph.AddTable(new FunctionConfig
            {
                Schema = [i64, i64],
                Default = DefaultVal.Fail,
                Merge = MergeFn.Primitive(merge, MergeFn.Old, MergeFn.New),
                Name = "acc",
            });

            for (var i = 0; i < count; i++)
                egraph.AddTerm(src, [Int(egraph, i)]);

            return (src, acc);
        }

        /// <summary>
        /// <c>src(k) =&gt; v = calls(k); set acc(0, 1)</c>: each of <paramref name="calls"/> is a right-hand-side call
        /// on the previous one's result.
        /// </summary>
        static EGraphRuleId CountingRule(EGraph egraph, string description, bool semiNaive, FunctionId src, FunctionId acc, params ExternalFunctionId[] calls)
        {
            var i64 = IntType(egraph);
            var rb = egraph.NewRule(description, semiNaive);
            var value = rb.NewVar(i64);
            rb.QueryTable(src, [value, rb.NewVar(ColumnTy.Id)], false);
            foreach (var call in calls)
                value = rb.CallExternalFunc(call, [value], i64, () => "call failed");

            rb.Set(acc, [egraph.BaseValueConstant<Int64Codec, long>(0), egraph.BaseValueConstant<Int64Codec, long>(1)]);
            return rb.Build();
        }

        [Fact]
        public void AFailedRunInTheParallelExecutorAppliesNothing()
        {
            // Full action batches run as tasks on four threads and flush as they finish; the failed run discards every
            // one of them.
            var egraph = new EGraph(new ParallelHeuristics(4));
            egraph.Database.ParallelRuleSetsOverride = true;
            const int N = 1000;
            var failing = true;
            var calls = 0;
            var gate = Register(egraph, (_, args) =>
            {
                if (Volatile.Read(ref failing) && Interlocked.Increment(ref calls) > N - 50)
                    throw new InvalidOperationException("gate closed");

                return args[0];
            });
            var (src, acc) = CountingTables(egraph, N, Add(egraph));
            var rule = CountingRule(egraph, "count", true, src, acc, gate);

            var t = egraph.NextTimestamp;
            Assert.Throws<InvalidOperationException>(() => egraph.RunRules(rule));
            Assert.Equal(0u, egraph.GetRuleInfo(rule).LastRunAt);
            Assert.Equal(t, egraph.NextTimestamp);
            egraph.FlushUpdates();
            Assert.Empty(Rows(egraph, acc));

            Volatile.Write(ref failing, false);
            var report = egraph.RunRules(rule);
            Assert.Equal(N, report.RuleSetReport.NumMatches("count"));
            Assert.Equal(N, Unint(egraph, Rows(egraph, acc).Single().Values[1]));
        }

        [Fact]
        public void APanicRecordedBeforeAnExceptionIsDroppedWithTheRun()
        {
            // In one batch, the first call produces nothing for key 0, which panics, and the second then throws on the
            // other keys. The failed run is undone, its panic too, so the next run neither throws nor misses a match.
            var egraph = NewEGraph();
            var failing = true;
            var zero = Int(egraph, 0).Rep;
            var partial = Register(egraph, (_, args) => failing && args[0] == zero ? null : args[0]);
            var gate = Register(egraph, (_, args) => failing ? throw new InvalidOperationException("gate closed") : args[0]);
            var (src, acc) = CountingTables(egraph, 10, Add(egraph));
            var rule = CountingRule(egraph, "count", true, src, acc, partial, gate);

            Assert.Throws<InvalidOperationException>(() => egraph.RunRules(rule));
            Assert.Equal(0u, egraph.GetRuleInfo(rule).LastRunAt);

            failing = false;
            var report = egraph.RunRules(rule);
            Assert.Equal(10, report.RuleSetReport.NumMatches("count"));
            Assert.Equal(10, Unint(egraph, Rows(egraph, acc).Single().Values[1]));
        }

        [Fact]
        public void AnExceptionWhileMergingKeepsTheWatermarks()
        {
            // The merge cannot be undone, so the watermarks stay advanced and the matches are not applied again; the
            // table whose merge threw drops the writes it had not applied.
            var egraph = NewEGraph();
            var failing = false;
            var merge = Add(egraph, () =>
            {
                if (failing)
                    throw new InvalidOperationException("merge failed");
            });
            var (src, acc) = CountingTables(egraph, 3, merge);
            var rule = CountingRule(egraph, "count", true, src, acc);
            var again = CountingRule(egraph, "count again", false, src, acc);

            Assert.Equal(3, egraph.RunRules(rule).RuleSetReport.NumMatches("count"));
            Assert.Equal(3, Unint(egraph, Rows(egraph, acc).Single().Values[1]));

            // The naive rule matches every src row again, and its writes collide with acc(0) in the merge, which throws.
            failing = true;
            var t = egraph.NextTimestamp;
            Assert.Throws<InvalidOperationException>(() => egraph.RunRules(rule, again));
            Assert.Equal(t, egraph.GetRuleInfo(rule).LastRunAt);
            Assert.Equal(t, egraph.GetRuleInfo(again).LastRunAt);
            Assert.Equal(3, Unint(egraph, Rows(egraph, acc).Single().Values[1]));

            failing = false;
            Assert.Equal(0, egraph.RunRules(rule).RuleSetReport.NumMatches("count"));
            Assert.Equal(3, Unint(egraph, Rows(egraph, acc).Single().Values[1]));
        }

        [Fact]
        public void ARuleListedTwiceRunsAgainWithTheNewWatermark()
        {
            var (egraph, edge, seen, ids) = Graph(3);
            var semi = CopyRule(egraph, edge, seen, true, "semi");
            var naive = CopyRule(egraph, edge, seen, false, "naive");
            egraph.AddValues([(edge, [ids[0], ids[1], ids[2]]), (edge, [ids[1], ids[2], ids[2]])]);

            var report = egraph.RunRules(semi, semi, naive, naive);
            Assert.Equal(2, report.RuleSetReport.NumMatches("semi"));
            Assert.Equal(4, report.RuleSetReport.NumMatches("naive"));
        }

        [Fact]
        public void ARunMintsOneIdPerMissingKey()
        {
            var fx = new RunFixture();
            var egraph = fx.EGraph;
            var n = Enumerable.Range(0, 3).Select(i => fx.Number(i)).ToArray();
            var c = n[0];

            // Every num row looks up add(c, c), which is missing: one id is minted, shared by every match.
            var rb = egraph.NewRule("mint", true);
            var k = rb.NewVar(egraph.GetFunctionInfo(fx.Num).Schema[0]);
            var id = rb.NewVar(ColumnTy.Id);
            rb.QueryTable(fx.Num, [k, id], false);
            var minted = rb.Lookup(fx.Add, [RuleEntry.Const(c, ColumnTy.Id), RuleEntry.Const(c, ColumnTy.Id)], () => "");
            rb.Set(fx.Same, [id, minted, minted]);
            var rule = rb.Build();

            var next = egraph.Database.ReadCounter(egraph.IdCounter);
            var report = egraph.RunRules(rule);
            Assert.Equal(3, report.RuleSetReport.NumMatches("mint"));
            Assert.Equal(next + 1, egraph.Database.ReadCounter(egraph.IdCounter));
            Assert.Single(Rows(egraph, fx.Add));
            Assert.Equal(3, Rows(egraph, fx.Same).Select(r => r.Values[1]).Count(v => v == new Value((uint)next)));
        }

        [Fact]
        public void RunRulesWithNoRulesIsAnIteration()
        {
            var egraph = NewEGraph();
            var t = egraph.NextTimestamp;
            var report = egraph.RunRules();
            Assert.False(report.Changed);
            Assert.Equal(t + 1, egraph.NextTimestamp);
        }

        [Fact]
        public void BaseValueConstantsCarryTheirType()
        {
            var egraph = NewEGraph();
            var i64 = IntType(egraph);
            var entry = egraph.BaseValueConstant<Int64Codec, long>(5);
            Assert.False(entry.IsVariable);
            Assert.Equal(Int(egraph, 5), entry.Constant);
            Assert.Equal(i64, entry.ConstantType);
        }

        [Fact]
        public void RulesRunWithTheEGraphsReportLevel()
        {
            var (egraph, edge, seen, ids) = Graph(2);
            var rule = CopyRule(egraph, edge, seen, true);
            egraph.AddValues([(edge, [ids[0], ids[1], ids[1]])]);
            egraph.ReportLevel = ReportLevel.WithPlan;
            var report = egraph.RunRules(rule);
            Assert.NotNull(report.RuleSetReport.RuleReports["copy"].Single().Plan);
        }

    }

}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Xunit;

using static Eggnog.Relations.Tests.BridgeTestUtil;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// Verification probes for the M10.3 fix stage (colored-contexts spec §10.2-§10.4, §14.1, §14.2; rule-layer E4): the
    /// root's egglog-compatible writes staged straight into database buffers, staging rolled back on a refusal, clears
    /// that drop staged rows, bounded staging retention, the progress map, repeat detection over long rule lists, the
    /// empty-run merge at the root through the egglog-compatible API, and the atomic contradiction designation.
    /// </summary>
    public class ContextFixVerifyTests
    {

        /// <summary>
        /// Adds a function over one id key whose output is an i64 merged by <paramref name="merge"/>.
        /// </summary>
        static FunctionId ValueFunction(ContextGraph g, MergeFn merge, string name) => g.EGraph.AddTable(new FunctionConfig
        {
            Schema = [ColumnTy.Id, g.I64],
            Default = DefaultVal.Fail,
            Merge = merge,
            Name = name,
        });

        /// <summary>
        /// The control for <see cref="TheRootAppliesWritesInTheOrderTheyWereStaged"/>: two writes through one API land in
        /// staging order, so the order-dependent merges <c>new</c> and <c>old</c> keep the later and the earlier value.
        /// </summary>
        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void OneApiAppliesWritesInStagingOrder(bool newWins)
        {
            var g = new ContextGraph();
            var f = ValueFunction(g, newWins ? MergeFn.New : MergeFn.Old, "f");
            var a = g.Root.FreshId();

            g.Root.StageRow(f, [a, g.Int(1)]);
            g.Root.StageRow(f, [a, g.Int(2)]);
            g.Root.FlushUpdates();
            Assert.Equal(g.Int(newWins ? 2 : 1), g.Root.LookupId(f, [a]));

            var b = g.Root.FreshId();
            g.EGraph.AddValues([(f, [b, g.Int(1)]), (f, [b, g.Int(2)])]);
            Assert.Equal(g.Int(newWins ? 2 : 1), g.Root.LookupId(f, [b]));
        }

        /// <summary>
        /// The root's two write APIs are applied in the order the host issued them. A row staged at the root through the
        /// context API (bridge-side, <see cref="HostWrites"/>) and then a row for the same key loaded through the
        /// egglog-compatible <see cref="EGraph.AddValues"/>: under <c>:merge new</c> the later row wins, under
        /// <c>:merge old</c> the earlier one. Before the M10.3 fix stage both went through <see cref="HostWrites"/> in
        /// staging order; that stage made <see cref="EGraphContext.AddValuesAtRoot"/> publish its database buffers before
        /// its flush replayed the rows staged earlier bridge-side, so the earlier write landed last. It now flushes the
        /// root's host writes first (colored-contexts spec §10.4).
        /// </summary>
        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void TheRootAppliesWritesInTheOrderTheyWereStaged(bool newWins)
        {
            var g = new ContextGraph();
            var f = ValueFunction(g, newWins ? MergeFn.New : MergeFn.Old, "f");
            var a = g.Root.FreshId();

            g.Root.StageRow(f, [a, g.Int(1)]);
            g.EGraph.AddValues([(f, [a, g.Int(2)])]);

            Assert.True(g.Root.IsClean);
            Assert.Equal(g.Int(newWins ? 2 : 1), g.Root.LookupId(f, [a]));
        }

        /// <summary>
        /// The context API's <see cref="EGraphContext.AddValues"/> stages each row as it validates it and drops the call's
        /// rows on a malformed one (<see cref="HostWrites.TruncateRows"/>): rows staged by earlier calls stay staged and
        /// land at the next flush; none of the refused call's rows do.
        /// </summary>
        [Fact]
        public void ARefusedAddValuesKeepsTheRowsStagedBeforeIt()
        {
            var g = new ContextGraph();
            var a = g.Root.FreshId();
            var b = g.Root.FreshId();
            var c = g.Root.FreshId();
            g.Assert(g.Root, g.Edge, a, b);
            g.Root.StageUnion(b, c);

            Assert.Throws<ArgumentException>(() => g.Root.AddValues([(g.Edge, [b, c, g.Unit]), (g.Mark, [a, g.Unit]), (g.Edge, [a])]));
            Assert.False(g.Root.IsClean);
            g.Root.FlushUpdates();

            Assert.Equal([$"{g.Root.Find(a).Rep} {g.Root.Find(b).Rep}"], ContextGraph.Keys(g.Root, g.Edge));
            Assert.Empty(ContextGraph.Keys(g.Root, g.Mark));
            Assert.Equal(g.Root.Find(b), g.Root.Find(c));
        }

        /// <summary>
        /// The same in a child, where the refusal is decision 2's (a function that is not a lattice): the child's rows
        /// staged before the refused call land, the call's own rows do not, and the child stays usable.
        /// </summary>
        [Fact]
        public void AChildsRefusedAddValuesKeepsTheRowsStagedBeforeIt()
        {
            var g = new ContextGraph();
            var last = ValueFunction(g, MergeFn.New, "last");
            var a = g.Root.FreshId();
            var b = g.Root.FreshId();
            g.Root.Freeze();

            using var child = g.Root.CreateChild();
            g.Assert(child, g.Mark, a);
            Assert.Throws<InvalidOperationException>(() => child.AddValues([(g.Mark, [b, g.Unit]), (last, [a, g.Int(2)])]));
            Assert.False(child.IsFaulted);
            child.FlushUpdates();

            Assert.Equal([$"{a.Rep}"], ContextGraph.Keys(child, g.Mark));
            Assert.Null(child.LookupId(last, [a]));
            child.CheckInvariants();
        }

        /// <summary>
        /// <see cref="HostWrites.RemoveRows"/> compacts one function's rows out of an arena of rows of several widths:
        /// the other functions' rows and the staged unions survive the clear, in order and intact.
        /// </summary>
        [Fact]
        public void AClearKeepsTheOtherFunctionsRowsAndTheUnions()
        {
            var g = new ContextGraph();
            var ids = Enumerable.Range(0, 8).Select(_ => g.Root.FreshId()).ToArray();
            g.Assert(g.Root, g.Edge, ids[0], ids[1]);
            g.Assert(g.Root, g.Mark, ids[2]);
            g.Root.StageRow(g.Low, [ids[3], g.Int(7)]);
            g.Assert(g.Root, g.Edge, ids[4], ids[5]);
            g.Root.StageUnion(ids[6], ids[7]);
            g.Assert(g.Root, g.Mark, ids[5]);
            g.Root.StageRow(g.Low, [ids[3], g.Int(4)]);

            g.Root.ClearTable(g.Edge);
            g.Assert(g.Root, g.Edge, ids[1], ids[2]);
            g.Root.FlushUpdates();

            Assert.Equal([$"{ids[1].Rep} {ids[2].Rep}"], ContextGraph.Keys(g.Root, g.Edge));
            Assert.Equal(new[] { $"{ids[2].Rep}", $"{ids[5].Rep}" }.OrderBy(s => s, StringComparer.Ordinal), ContextGraph.Keys(g.Root, g.Mark));
            Assert.Equal(g.Int(4), g.Root.LookupId(g.Low, [ids[3]]));
            Assert.Equal(g.Root.Find(ids[6]), g.Root.Find(ids[7]));
            g.Root.CheckInvariants();
        }

        /// <summary>
        /// A context that staged a batch larger than the retention limits releases its arenas at the flush
        /// (<see cref="HostWrites.Clear"/>) and stages the next batches, rows and unions, from empty storage.
        /// </summary>
        [Fact]
        public void StagingWorksAgainAfterALargeBatchIsReleased()
        {
            var g = new ContextGraph();
            var count = 3 * HostWrites.RetainedRows;
            var ids = Enumerable.Range(0, count + 4).Select(_ => g.Root.FreshId()).ToArray();
            for (var i = 0; i < count; i++)
                g.Assert(g.Root, g.Mark, ids[i]);

            for (var i = 0; i + 1 < count; i += 2)
                g.Root.StageUnion(ids[i], ids[i + 1]);

            g.Root.FlushUpdates();
            Assert.Equal(count / 2, ContextGraph.Keys(g.Root, g.Mark).Length);

            g.Root.Freeze();
            using var child = g.Root.CreateChild();
            for (var round = 0; round < 3; round++)
            {
                var x = child.FreshId();
                g.Assert(child, g.Edge, ids[count], x);
                child.StageUnion(ids[count + 1], x);
                child.FlushUpdates();
                Assert.Equal(child.Find(ids[count + 1]), child.Find(x));
            }

            for (var i = 0; i < count; i++)
                g.Assert(child, g.Edge, ids[i], ids[count + 2]);

            child.FlushUpdates();
            g.Assert(child, g.Edge, ids[count + 3], ids[count + 3]);
            child.FlushUpdates();
            // The three round edges collapse into one (their targets were unioned), the bulk edges into one per class.
            Assert.Equal(count / 2 + 2, ContextGraph.Keys(child, g.Edge).Length);
            child.CheckInvariants();
        }

        /// <summary>
        /// The progress map (§10.2, X18) keeps every rule's watermark through its growth and rehashing: a child that runs
        /// 40 semi-naive rules over one edge, then again with nothing new, matches nothing the second time, and after one
        /// more edge every rule matches exactly that edge once.
        /// </summary>
        [Fact]
        public void TheProgressMapKeepsEveryRulesWatermark()
        {
            var g = new ContextGraph();
            var rules = Enumerable.Range(0, 40).Select(i => g.EdgeToPath(name: $"copy{i}")).ToArray();
            var a = g.Root.FreshId();
            var b = g.Root.FreshId();
            var c = g.Root.FreshId();
            g.Assert(g.Root, g.Edge, a, b);
            g.Root.FlushUpdates();
            g.Root.Freeze();

            using var child = g.Root.CreateChild(new ContextOptions { InheritWatermarks = false });
            var first = child.RunRules(rules);
            var again = child.RunRules(rules);
            g.Assert(child, g.Edge, b, c);
            child.FlushUpdates();
            var third = child.RunRules(rules);
            for (var i = 0; i < rules.Length; i++)
            {
                Assert.Equal(1, first.RuleSetReport.NumMatches($"copy{i}"));
                Assert.Equal(0, again.RuleSetReport.NumMatches($"copy{i}"));
                Assert.Equal(1, third.RuleSetReport.NumMatches($"copy{i}"));
                Assert.True(child.CommittedLastRunAt(rules[i], g.EGraph.GetRuleInfo(rules[i]).Generation) > 0);
            }

            child.CheckInvariants();
        }

        /// <summary>
        /// A rule listed again in a list longer than the scan limit (the dictionary path of the repeat detection) runs
        /// with the run's own timestamp, as in a short list (<c>EGraphRulesTests.ARuleListedTwiceRunsAgainWithTheNewWatermark</c>):
        /// its later listings match nothing, and a naive rule listed twice matches every row each time.
        /// </summary>
        [Fact]
        public void ARuleListedAgainInALongListRunsWithTheRunsTimestamp()
        {
            var g = new ContextGraph();
            var semi = g.EdgeToPath(name: "semi");
            var naive = g.EdgeToPath(semiNaive: false, name: "naive");
            var fillers = Enumerable.Range(0, 20).Select(i => g.EdgeToPath(name: $"filler{i}")).ToArray();
            var ids = Enumerable.Range(0, 3).Select(_ => g.Root.FreshId()).ToArray();
            g.Assert(g.Root, g.Edge, ids[0], ids[1]);
            g.Assert(g.Root, g.Edge, ids[1], ids[2]);
            g.Root.FlushUpdates();

            var list = new List<EGraphRuleId> { semi, naive };
            list.AddRange(fillers);
            list.Add(semi);
            list.Add(naive);
            list.Add(semi);
            var report = g.Root.RunRules(list.ToArray());

            Assert.Equal(2, report.RuleSetReport.NumMatches("semi"));
            Assert.Equal(4, report.RuleSetReport.NumMatches("naive"));
            Assert.Equal(2, report.RuleSetReport.NumMatches("filler19"));
            Assert.Equal(0, g.Root.RunRules(list.ToArray()).RuleSetReport.NumMatches("semi"));
        }

        /// <summary>
        /// The empty-run merge (§10.3, X42, map §8.1 row 26) through the e-graph's egglog-compatible
        /// <see cref="EGraph.RunRules"/>: a row staged raw at the root before runs whose rule sets are empty lands at a
        /// run's timestamp, so a later run matches it.
        /// </summary>
        [Fact]
        public void TheEgglogRunMergesARowStagedRawBeforeAnEmptyRun()
        {
            var g = new ContextGraph();
            var rule = g.EdgeToPath();
            var a = g.Root.FreshId();
            var b = g.Root.FreshId();
            var c = g.Root.FreshId();
            g.EGraph.AddValues([(g.Edge, [b, c, g.Unit])]);
            g.EGraph.RunRules(rule);

            var edge = new TableAction(g.EGraph, g.Edge);
            g.EGraph.WithExecutionState(state =>
            {
                edge.Insert(state, [a.Rep, b.Rep, g.Unit.Rep]);
                return 0;
            });

            var empty = g.EGraph.RunRules(rule);
            Assert.True(empty.Changed);
            g.EGraph.RunRules(rule);
            g.EGraph.RunRules(rule);
            Assert.Equal(g.Unit, g.Root.LookupId(g.Path, [a, b]));
        }

        /// <summary>
        /// E4 made atomic with child creation: a designation racing the first child of a frozen root either fails, or
        /// lands before the child's creation check, which then sees the contradiction row the root holds and stops the
        /// case (§6.9 item 3, C5/I25). Never a designation that succeeds beside a case that missed it.
        /// </summary>
        [Fact]
        public void ADesignationRacingTheFirstChildIsSeenByItOrRefused()
        {
            var designated = 0;
            var refused = 0;
            for (var i = 0; i < 200; i++)
            {
                var g = new ContextGraph();
                g.Assert(g.Root, g.Bottom);
                g.Root.FlushUpdates();
                g.Root.Freeze();

                using var barrier = new Barrier(2);
                EGraphContext? child = null;
                var create = Task.Run(() =>
                {
                    barrier.SignalAndWait();
                    child = g.Root.CreateChild(ContextOptions.Case);
                });
                var designate = Task.Run(() =>
                {
                    barrier.SignalAndWait();
                    try
                    {
                        g.EGraph.DesignateContradiction(g.Bottom);
                        return true;
                    }
                    catch (InvalidOperationException)
                    {
                        return false;
                    }
                });

                create.Wait();
                var won = designate.Result;
                using (child)
                {
                    if (won)
                    {
                        designated++;
                        Assert.True(child!.IsStopped, $"iteration {i}: the designation succeeded, yet the case did not stop");
                    }
                    else
                    {
                        refused++;
                        Assert.False(child!.IsStopped);
                    }
                }
            }

            Assert.Equal(200, designated + refused);
        }

    }

}

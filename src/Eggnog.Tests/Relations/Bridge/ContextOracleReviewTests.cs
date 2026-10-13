using System;
using System.Linq;
using System.Threading;

using Xunit;

using static Eggnog.Relations.Tests.BridgeTestUtil;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// Oracle gaps found by mutation testing of colors M10.3 (colored-contexts spec §10.3, §10.4, §10.8, §10.9, §12.7;
    /// rule-layer E7, E10). Each test kills a plausible bug that every M10.3 suite, the random differentials included,
    /// let through: a failing flush that leaves a child usable, progress committed after a recorded failure when the merge
    /// throws, a panic hidden behind a stop or a post-run rebuild, a <c>CreateChild</c> that does not flush the parent's
    /// staged writes, a freeze that leaves its rebuild to the storage freeze, a <c>ConcurrentChildren</c> degree that
    /// ignores the widest child, and C1 checker items no test triggered.
    /// </summary>
    public class ContextOracleReviewTests
    {

        /// <summary>
        /// X12 for host writes: an exception escaping a child's flush (here a merge primitive that throws while the child's
        /// staged rows merge) faults the child, which then refuses every write; the frozen parent is untouched.
        /// </summary>
        [Fact]
        public void AThrowingFlushFaultsTheChild()
        {
            var g = new ContextGraph();
            var failing = false;
            var boom = Register(g.EGraph, (_, args) => failing ? throw new InvalidOperationException("merge failed") : args[0]);
            var f = g.EGraph.AddTable(new FunctionConfig
            {
                Schema = [ColumnTy.Id, g.I64],
                Default = DefaultVal.Fail,
                Merge = MergeFn.Primitive(boom, MergeFn.Old, MergeFn.New).AsLattice(),
                Name = "boomy",
            });
            var a = g.Root.FreshId();
            g.Root.AddValues([(f, [a, g.Int(1)])]);
            g.Root.Freeze();

            var child = g.Root.CreateChild();
            failing = true;
            child.StageRow(f, [a, g.Int(2)]);
            child.StageRow(f, [a, g.Int(3)]);
            Assert.Throws<InvalidOperationException>(() => child.FlushUpdates());
            Assert.True(child.IsFaulted);
            Assert.Throws<InvalidOperationException>(() => child.StageRow(f, [a, g.Int(4)]));
            Assert.Throws<InvalidOperationException>(() => child.FlushUpdates());

            failing = false;
            Assert.True(g.Root.TryLookup(f, [a], out var value));
            Assert.Equal(g.Int(1), value);
            child.Dispose();
            Assert.True(child.IsDisposed);
        }

        /// <summary>
        /// X18 when the merge throws after a failure was recorded: a panic action records a failure in the search, then a
        /// merge primitive throws. No progress is committed (the documented rule: "committed unless a failure was
        /// recorded"), so neither rule's watermark moves.
        /// </summary>
        [Fact]
        public void AMergeExceptionAfterARecordedFailureCommitsNoProgress()
        {
            var g = new ContextGraph();
            var failing = false;
            var boom = Register(g.EGraph, (_, args) => failing ? throw new InvalidOperationException("merge failed") : args[1]);
            var acc = g.EGraph.AddTable(new FunctionConfig
            {
                Schema = [ColumnTy.Id, g.I64],
                Default = DefaultVal.Fail,
                Merge = MergeFn.Primitive(boom, MergeFn.Old, MergeFn.New),
                Name = "acc",
            });
            var a = g.Root.FreshId();
            g.Root.AddValues([(g.Mark, [a, g.Unit]), (acc, [a, g.Int(1)])]);

            var wb = g.EGraph.NewRule("write", true);
            var x = wb.NewVar(ColumnTy.Id);
            wb.QueryTable(g.Mark, [x, wb.NewVar(g.UnitType)], false);
            wb.Set(acc, [x, RuleEntry.Const(g.Int(2), g.I64)]);
            var write = wb.Build();

            var pb = g.EGraph.NewRule("panics", true);
            pb.QueryTable(g.Mark, [pb.NewVar(ColumnTy.Id), pb.NewVar(g.UnitType)], false);
            pb.Panic("recorded");
            var panics = pb.Build();

            failing = true;
            Assert.Throws<InvalidOperationException>(() => g.Root.RunRules(write, panics));
            Assert.Equal(0u, g.EGraph.GetRuleInfo(write).LastRunAt);
            Assert.Equal(0u, g.EGraph.GetRuleInfo(panics).LastRunAt);
            Assert.Equal(0u, g.Root.CommittedLastRunAt(write, g.EGraph.GetRuleInfo(write).Generation));
        }

        /// <summary>
        /// §10.3: a failure recorded by a run that also stopped the context is thrown as <see cref="PanicException"/>; the
        /// stop does not hide it. A case's rule derives the contradiction and, in the same run, writes an assert-eq conflict.
        /// </summary>
        [Fact]
        public void APanicInTheRunThatStopsACaseIsThrown()
        {
            var g = new ContextGraph();
            g.EGraph.DesignateContradiction(g.Bottom);
            var keyed = AssertEqFunction(g);
            var a = g.Root.FreshId();
            g.Root.AddValues([(keyed, [a, g.Int(1)])]);

            var rb = g.EGraph.NewRule("stop-and-conflict", true);
            var x = rb.NewVar(ColumnTy.Id);
            rb.QueryTable(g.Mark, [x, rb.NewVar(g.UnitType)], false);
            rb.Set(keyed, [x, RuleEntry.Const(g.Int(2), g.I64)]);
            rb.Set(g.Bottom, [RuleEntry.Const(g.Unit, g.UnitType)]);
            var rule = rb.Build();
            g.Root.Freeze();

            using var @case = g.Root.CreateChild(ContextOptions.Case);
            g.Assert(@case, g.Mark, a);
            @case.FlushUpdates();
            Assert.Throws<PanicException>(() => @case.RunRules(rule));
            Assert.True(@case.IsStopped);
            Assert.False(@case.IsFaulted);
        }

        /// <summary>
        /// A failure the post-run rebuild records (an assert-eq conflict the run's union exposes) is thrown by that run, at
        /// the root and in a child, and is taken: the next run does not throw it again.
        /// </summary>
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void AFailureThePostRunRebuildRecordsIsThrownByTheRun(bool inChild)
        {
            var g = new ContextGraph();
            var keyed = AssertEqFunction(g);
            var a = g.Root.FreshId();
            var b = g.Root.FreshId();
            g.Root.AddValues([(keyed, [a, g.Int(1)]), (keyed, [b, g.Int(2)]), (g.Edge, [a, b, g.Unit])]);
            var unite = g.UnionEdges();
            EGraphContext? child = null;
            if (inChild)
            {
                g.Root.Freeze();
                child = g.Root.CreateChild();
            }

            var context = child ?? g.Root;
            Assert.Throws<PanicException>(() => context.RunRules(unite));
            Assert.Equal(context.Find(a), context.Find(b));
            context.RunRules(unite);
            child?.Dispose();
        }

        /// <summary>
        /// §10.4 and §8.2 item 24: <c>CreateChild</c> of a writable context flushes the host writes staged on it first, so
        /// the child starts as a copy of everything the parent was given, and both are readable at once.
        /// </summary>
        [Fact]
        public void CreateChildFlushesTheParentsStagedWrites()
        {
            var g = new ContextGraph();
            var a = g.Root.FreshId();
            var b = g.Root.FreshId();
            g.Assert(g.Root, g.Edge, a, b);
            g.Root.StageUnion(a, b);
            Assert.False(g.Root.IsClean);

            using var child = g.Root.CreateChild();
            Assert.True(g.Root.IsClean);
            Assert.True(child.IsClean);
            Assert.Equal(1, child.TableSize(g.Edge));
            Assert.Equal(child.Find(a), child.Find(b));
            Assert.Equal(1, g.Root.TableSize(g.Edge));
            child.CheckInvariants();
        }

        /// <summary>
        /// §10.8 step 3: the bridge freeze rebuilds and takes the failure its rebuild records as
        /// <see cref="PanicException"/>, before the storage freeze, which would refuse an untaken failure with a plain
        /// <see cref="InvalidOperationException"/>. The union is merged raw (staged through an execution state), so no flush
        /// or earlier rebuild exposes the conflict.
        /// </summary>
        [Fact]
        public void FreezeTakesTheFailureItsRebuildRecordsAsAPanic()
        {
            var g = new ContextGraph();
            var keyed = AssertEqFunction(g);
            var a = g.Root.FreshId();
            var b = g.Root.FreshId();
            g.Root.AddValues([(keyed, [a, g.Int(1)]), (keyed, [b, g.Int(2)])]);
            g.Root.WithExecutionState(state =>
            {
                state.StageInsert(g.EGraph.UnionFindTable, [a.Rep, b.Rep, g.EGraph.NextTimestamp]);
                return 0;
            });

            Assert.Throws<PanicException>(() => g.Root.Freeze());
            Assert.False(g.Root.IsFrozen);
            g.Root.Freeze();
            Assert.True(g.Root.IsFrozen);
            g.Root.CheckInvariants();
        }

        /// <summary>
        /// §10.9: <c>ConcurrentChildren.Run</c> runs <c>max(1, Total / widest child DOP)</c> bodies at once whatever the
        /// budget holds. Each body hands its lease back while it sleeps; a run with more workers than that degree would let
        /// a waiting worker take the freed threads and start another body.
        /// </summary>
        [Theory]
        [InlineData(4, 1)]
        [InlineData(2, 2)]
        public void TheDegreeIsTheBudgetOverTheWidestChild(int childThreads, int degree)
        {
            const int Total = 4;
            var g = new ContextGraph(new ParallelHeuristics(Total));
            g.Root.Freeze();
            var options = new ContextOptions { Parallelism = new ParallelHeuristics(childThreads) };
            var children = Enumerable.Range(0, 8).Select(_ => g.Root.CreateChild(options)).ToArray();
            var budget = g.EGraph.ParallelismBudget;
            var running = 0;
            var peak = 0;

            ConcurrentChildren.Run(children, child =>
            {
                var now = Interlocked.Increment(ref running);
                for (var seen = Volatile.Read(ref peak); now > seen && Interlocked.CompareExchange(ref peak, now, seen) != seen; seen = Volatile.Read(ref peak))
                {
                }

                budget.Release(childThreads);
                Thread.Sleep(50);
                _ = budget.Lease(childThreads);
                Interlocked.Decrement(ref running);
            });

            Assert.InRange(peak, 1, degree);
            Assert.Equal(Total, budget.Available);
            foreach (var child in children)
                child.Dispose();
        }

        /// <summary>
        /// C1: a committed run above the context's semi-naive floor (but not after the clock) is reported twice: as the
        /// recorded progress and as the watermark a run would use.
        /// </summary>
        [Fact]
        public void C1CatchesProgressAboveTheFloor()
        {
            var g = new ContextGraph();
            var rule = g.EdgeToPath();
            g.Root.RunRules(rule);
            var info = g.EGraph.GetRuleInfo(rule);
            var floor = g.Root.Context.SemiNaiveFloor;
            Assert.True(floor + 1 <= g.EGraph.NextTimestamp);
            g.Root.SetProgress(rule, new RuleProgress(floor + 1, null, info.Generation));

            var error = Assert.Throws<ContextInvariantException>(() => g.Root.CheckInvariants());
            Assert.Contains(error.Violations, v => v.StartsWith("C1:") && v.Contains("above the context's semi-naive floor"));
            Assert.Contains(error.Violations, v => v.StartsWith("C1:") && v.Contains("would run from"));
            Assert.DoesNotContain(error.Violations, v => v.Contains("after the clock"));
        }

        /// <summary>
        /// C1: a child's own progress older than its creation is reported.
        /// </summary>
        [Fact]
        public void C1CatchesAChildsProgressBeforeItsCreation()
        {
            var g = new ContextGraph();
            var rule = g.EdgeToPath();
            g.Root.RunRules(rule);
            g.Root.Freeze();
            using var child = g.Root.CreateChild();
            child.RunRules(rule);
            child.CheckInvariants();
            var info = g.EGraph.GetRuleInfo(rule);
            Assert.True(child.CreatedAt > 0);
            child.SetProgress(rule, new RuleProgress(child.CreatedAt - 1, null, info.Generation));

            var error = Assert.Throws<ContextInvariantException>(() => child.CheckInvariants());
            Assert.Contains(error.Violations, v => v.StartsWith("C1:") && v.Contains("before the context was created"));
        }

        /// <summary>
        /// A relation <c>keyed(id) -&gt; i64</c> whose merge asserts the outputs are equal: a conflict records a failure.
        /// </summary>
        static FunctionId AssertEqFunction(ContextGraph g)
        {
            return g.EGraph.AddTable(new FunctionConfig
            {
                Schema = [ColumnTy.Id, g.I64],
                Default = DefaultVal.Fail,
                Merge = MergeFn.AssertEq,
                Name = "keyed",
            });
        }

    }

}

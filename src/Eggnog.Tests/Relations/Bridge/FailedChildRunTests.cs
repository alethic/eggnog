using System;
using System.Linq;
using System.Threading;

using Xunit;

using static Eggnog.Relations.Tests.BridgeTestUtil;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// An exception escaping a child's <c>RunRuleSet</c>, <c>ExecuteRuleSet</c> or flush faults the child, which then
    /// refuses every write: a failed merge may leave hides whose copies never merged, which cannot be repaired, and a
    /// failed search, which never hides, faults it as well. A search that ran out of ids is the one exception
    /// (<c>StoreExhaustionTests</c>). The writes the failed search staged, in the layers it created too, are
    /// discarded, and <c>Database.MergeAll</c> and <c>MergeTable</c> refuse a faulted child, so nothing a failed run
    /// staged can ever merge. A recorded failure (a panic) is not an exception and faults nothing.
    /// </summary>
    public class FailedChildRunTests
    {

        /// <summary>
        /// A parallel child run whose external throws after many action batches flushed into a layer the search created
        /// (a layer whose creation waits for the merge): the child is faulted, that layer holds nothing staged, its
        /// database refuses to merge, the parent is untouched, and the child can be disposed.
        /// </summary>
        [Fact]
        public void AThrowingRunFaultsTheChildAndDiscardsWhatItStagedInNewLayers()
        {
            var g = new ContextGraph(new ParallelHeuristics(4));
            const int N = 1000;
            var calls = 0;
            var gate = Register(g.EGraph, (_, args) => Interlocked.Increment(ref calls) > N - 50 ? throw new InvalidOperationException("gate closed") : args[0]);
            var rb = g.EGraph.NewRule("gated", true);
            var x = rb.NewVar(ColumnTy.Id);
            rb.QueryTable(g.Mark, [x, rb.NewVar(g.UnitType)], false);
            var passed = rb.CallExternalFunc(gate, [x], ColumnTy.Id, () => "gate");
            rb.Set(g.Path, [passed, passed, RuleEntry.Const(g.Unit, g.UnitType)]);
            var rule = rb.Build();
            for (var i = 0; i < N; i++)
                g.Assert(g.Root, g.Mark, g.Root.FreshId());

            g.Root.FlushUpdates();
            g.Root.Freeze();

            using var child = g.Root.CreateChild(new ContextOptions { Parallelism = new ParallelHeuristics(4) });
            child.Context.Database.ParallelRuleSetsOverride = true;
            var pathTable = g.EGraph.GetTable(g.Path);
            Assert.Null(child.Context.Tables!.TryGetLayer(pathTable));

            Assert.Throws<InvalidOperationException>(() => child.RunRules(rule));
            Assert.True(child.IsFaulted);
            Assert.True(calls > N - 50);

            // The layer the search created is still deferred, and empty of staged writes.
            var layer = child.Context.Tables.TryGetLayer(pathTable);
            Assert.NotNull(layer);
            Assert.False(layer!.Own.HasPendingWrites);
            Assert.Equal(0, layer.Own.Count);

            // Nothing can merge it; every operation but Dispose is refused.
            Assert.Throws<InvalidOperationException>(() => child.Context.Database.MergeAll());
            Assert.Throws<InvalidOperationException>(() => child.Context.Database.MergeTable(pathTable));
            Assert.Throws<InvalidOperationException>(() => child.FlushUpdates());
            Assert.Throws<InvalidOperationException>(() => child.RunRules(rule));
            Assert.Equal(0, g.Root.TableSize(g.Path));
            child.Dispose();
            Assert.True(child.IsDisposed);
        }

        /// <summary>
        /// The rule layer's queries call <c>ExecuteRuleSet</c> directly: an exception there faults the child too, and
        /// its staged writes never merge.
        /// </summary>
        [Fact]
        public void AThrowingExecuteRuleSetFaultsTheChild()
        {
            var g = new ContextGraph();
            var boom = Register(g.EGraph, (_, _) => throw new InvalidOperationException("boom"));
            g.Assert(g.Root, g.Mark, g.Root.FreshId());
            g.Root.FlushUpdates();
            g.Root.Freeze();

            using var child = g.Root.CreateChild();
            var database = child.Context.Database;
            var markTable = g.EGraph.GetTable(g.Mark);
            var pathTable = g.EGraph.GetTable(g.Path);
            var rsb = database.NewRuleSet();
            var q = rsb.NewRule();
            var v = q.NewVariable("v");
            var u = q.NewVariable("u");
            var t = q.NewVariable("t");
            q.AddAtom(markTable, [v, u, t]);
            var r = q.Build();
            var ts = r.ReadCounter(g.EGraph.TimestampCounter);
            r.Insert(pathTable, [v, v, u, ts]);
            r.CallExternal(boom, [v]);
            r.Build("throws");
            using var ruleSet = rsb.Build();

            Assert.Throws<InvalidOperationException>(() => database.ExecuteRuleSet(ruleSet, ReportLevel.TimeOnly, g.EGraph.NextTimestamp));
            Assert.True(child.IsFaulted);
            Assert.Throws<InvalidOperationException>(() => database.MergeAll());
            Assert.False(child.Context.Tables!.TryGetLayer(pathTable)?.Own.HasPendingWrites ?? false);
        }

        /// <summary>
        /// A panic is a recorded failure, not an exception: the child is not faulted, its run committed its progress, as
        /// egglog sets <c>last_run_at</c> before the run, and it keeps working.
        /// </summary>
        [Fact]
        public void APanicDoesNotFaultTheChild()
        {
            var g = new ContextGraph();
            var rb = g.EGraph.NewRule("panics", true);
            var x = rb.NewVar(ColumnTy.Id);
            rb.QueryTable(g.Mark, [x, rb.NewVar(g.UnitType)], false);
            rb.Set(g.Path, [x, x, RuleEntry.Const(g.Unit, g.UnitType)]);
            rb.Panic("stop");
            var rule = rb.Build();
            g.Root.Freeze();

            using var child = g.Root.CreateChild();
            var a = child.FreshId();
            g.Assert(child, g.Mark, a);
            child.FlushUpdates();
            Assert.Throws<PanicException>(() => child.RunRules(rule));
            Assert.False(child.IsFaulted);
            Assert.NotEqual(0u, child.CommittedLastRunAt(rule, g.EGraph.GetRuleInfo(rule).Generation));

            // What merged before the stop stays (egglog's partial run), and the child is still usable.
            child.CheckInvariants();
            g.Assert(child, g.Mark, child.FreshId());
            child.FlushUpdates();
            Assert.Equal(2, child.TableSize(g.Mark));
        }

        /// <summary>
        /// A failure the root's rebuild or settle records during <see cref="EGraphContext.CreateChild"/>, or one
        /// pending on the parent, is taken right after: the new child is disposed, <see cref="PanicException"/> is
        /// thrown, and the parent stays usable.
        /// </summary>
        [Fact]
        public void CreateChildTakesTheParentsFailure()
        {
            var g = new ContextGraph();
            var keyed = g.EGraph.AddTable(new FunctionConfig
            {
                Schema = [ColumnTy.Id, g.I64],
                Default = DefaultVal.Fail,
                Merge = MergeFn.AssertEq,
                Name = "keyed",
            });
            var a = g.EGraph.FreshId();
            g.EGraph.AddValues([(keyed, [a, g.Int(1)])]);

            // The egglog API leaves the conflict's panic for the next run (egglog's flush_updates).
            g.EGraph.AddValues([(keyed, [a, g.Int(2)])]);
            var error = Assert.Throws<PanicException>(() => g.Root.CreateChild());
            Assert.Contains("keyed", error.PanicMessage);
            Assert.Equal(0, g.Root.Context.ChildCount);

            using var child = g.Root.CreateChild();
            Assert.Equal(1, g.Root.Context.ChildCount);
            child.CheckInvariants();
        }

        /// <summary>
        /// An exception escaping a child's flush (here a merge primitive that throws while the child's staged rows
        /// merge) faults the child, which then refuses every write; the frozen parent is untouched.
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

    }

}

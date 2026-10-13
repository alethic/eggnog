using System;
using System.Linq;
using System.Threading;

using Xunit;

using static Eggnog.Relations.Tests.BridgeTestUtil;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// Tests for egglog-level panics (egglog-bridge/src/lib.rs:1435-1515): the panic functions, the side channel, the
    /// interning of messages, and how an <see cref="EGraph"/> and its contexts report them, a failure recorded by a run
    /// that also stopped its context or by the rebuild after a run included.
    /// </summary>
    public class PanicTests
    {

        /// <summary>
        /// egglog's <c>panic_functions_trigger_early_stop</c> (egglog-bridge/src/tests.rs:1480-1506).
        /// </summary>
        [Fact]
        public void PanicFunctionsTriggerEarlyStop()
        {
            var db = new Database(ParallelHeuristics.Serial);

            var channel = new SideChannel<string>();
            var panic = new PanicFunction("panic", channel);
            var stopped = db.WithExecutionState(state =>
            {
                Assert.False(state.ShouldStop);
                Assert.False(panic.TryInvoke(state, ReadOnlySpan<uint>.Empty, out _));
                return state.ShouldStop;
            });
            Assert.True(stopped);
            Assert.True(channel.TryPeek(out var message));
            Assert.Equal("panic", message);

            var lazyChannel = new SideChannel<string>();
            var lazy = new LazyPanicFunction(() => "lazy panic", lazyChannel);
            Assert.False(lazy.IsMessageCreated);
            stopped = db.WithExecutionState(state =>
            {
                Assert.False(state.ShouldStop);
                Assert.False(lazy.TryInvoke(state, ReadOnlySpan<uint>.Empty, out _));
                return state.ShouldStop;
            });
            Assert.True(stopped);
            Assert.True(lazyChannel.TryPeek(out message));
            Assert.Equal("lazy panic", message);
        }

        [Fact]
        public void TheFirstMessageWins()
        {
            var db = new Database(ParallelHeuristics.Serial);
            var channel = new SideChannel<string>();
            var first = new PanicFunction("first", channel);
            var built = 0;
            var second = new LazyPanicFunction(() =>
            {
                built++;
                return "second";
            }, channel);

            db.WithExecutionState(state =>
            {
                first.TryInvoke(state, ReadOnlySpan<uint>.Empty, out _);
                second.TryInvoke(state, ReadOnlySpan<uint>.Empty, out _);
                return 0;
            });

            // The lazy message is not even built while a message is recorded.
            Assert.Equal(0, built);
            Assert.True(channel.TryTake(out var message));
            Assert.Equal("first", message);
            Assert.False(channel.HasValue);
            Assert.False(channel.TryTake(out _));

            db.WithExecutionState(state => second.TryInvoke(state, ReadOnlySpan<uint>.Empty, out _));
            db.WithExecutionState(state => second.TryInvoke(state, ReadOnlySpan<uint>.Empty, out _));
            Assert.Equal(1, built);
            Assert.True(channel.TryTake(out message));
            Assert.Equal("second", message);
        }

        [Fact]
        public void PanicsTakeNoArguments()
        {
            var db = new Database(ParallelHeuristics.Serial);
            var channel = new SideChannel<string>();
            var panic = new PanicFunction("p", channel);
            Assert.Throws<ArgumentException>(() => db.WithExecutionState(state => panic.TryInvoke(state, [1u], out _)));
        }

        [Fact]
        public void SideChannelSetReplaces()
        {
            var channel = new SideChannel<int>();
            Assert.True(channel.TrySet(1));
            Assert.False(channel.TrySet(2));
            channel.Set(3);
            Assert.True(channel.TryPeek(out var value));
            Assert.Equal(3, value);
        }

        [Fact]
        public void NewPanicInternsByMessageAndLazyPanicsAreNotInterned()
        {
            var egraph = NewEGraph();
            var a = egraph.NewPanic("a");
            Assert.Equal(a, egraph.NewPanic("a"));
            Assert.NotEqual(a, egraph.NewPanic("b"));
            Assert.NotEqual(egraph.NewPanicLazy(() => "a"), egraph.NewPanicLazy(() => "a"));
            Assert.IsType<PanicFunction>(egraph.Database.GetExternalFunction(a));
        }

        [Fact]
        public void APanicIsReportedByTheNextIteration()
        {
            var egraph = NewEGraph();
            var panic = egraph.NewPanic("boom");
            egraph.WithExecutionState(state => state.TryCallExternalFunction(panic, ReadOnlySpan<uint>.Empty, out _));
            Assert.True(egraph.PanicChannel.HasValue);

            var error = Assert.Throws<PanicException>(() => egraph.RunIteration(_ => RuleSetReport.Empty));
            Assert.Equal("boom", error.PanicMessage);

            // The message is taken: the next iteration succeeds.
            egraph.RunIteration(_ => RuleSetReport.Empty);
        }

        [Fact]
        public void LazyPanicsReportTheirMessage()
        {
            var egraph = NewEGraph();
            var panic = egraph.NewPanicLazy(() => "lazy " + 42);
            var error = Assert.Throws<PanicException>(() => egraph.RunIteration(ts =>
            {
                egraph.Database.WithExecutionState(state => state.TryCallExternalFunction(panic, ReadOnlySpan<uint>.Empty, out _), ts);
                return RuleSetReport.Empty;
            }));
            Assert.Equal("lazy 42", error.PanicMessage);
        }

        [Fact]
        public void FailuresReportedThroughTheEngineChannelAreReportedToo()
        {
            var egraph = NewEGraph();
            egraph.WithExecutionState(state =>
            {
                state.ReportFailure("engine failure");
                return 0;
            });
            var error = Assert.Throws<PanicException>(() => egraph.RunIteration(_ => RuleSetReport.Empty));
            Assert.Equal("engine failure", error.PanicMessage);

            // When both channels hold a failure, the panic channel's is reported and the iteration reports nothing
            // else.
            var panic = egraph.NewPanic("panic first");
            egraph.WithExecutionState(state =>
            {
                state.TryCallExternalFunction(panic, ReadOnlySpan<uint>.Empty, out _);
                state.ReportFailure("engine second");
                return 0;
            });
            error = Assert.Throws<PanicException>(() => egraph.RunIteration(_ => RuleSetReport.Empty));
            Assert.Equal("panic first", error.PanicMessage);
            egraph.RunIteration(_ => RuleSetReport.Empty);
        }

        [Fact]
        public void FreeingAPanicForgetsItsMessage()
        {
            var egraph = NewEGraph();
            var panic = egraph.NewPanic("temporary");
            egraph.FreeExternalFunction(panic);

            // The freed id is reused by the next registration; the message must not map to it any more.
            var other = Register(egraph, (_, _) => 1u);
            Assert.Equal(panic, other);
            var again = egraph.NewPanic("temporary");
            Assert.NotEqual(other, again);
            Assert.IsType<PanicFunction>(egraph.Database.GetExternalFunction(again));
        }

        [Fact]
        public void FreeingOtherFunctionsKeepsTheCacheAndReinternedPanicsCanBeFreedAgain()
        {
            var egraph = NewEGraph();
            var baseline = egraph.PanicFunctionCount;
            var keep = egraph.NewPanic("keep");

            // Freeing a function that is not an interned panic leaves the cache alone.
            var other = Register(egraph, (_, _) => 1u);
            egraph.FreeExternalFunction(other);
            Assert.Equal(keep, egraph.NewPanic("keep"));
            Assert.Equal(baseline + 1, egraph.PanicFunctionCount);

            // A message interned again after a free maps to its new id, and freeing that id forgets it again.
            var first = egraph.NewPanic("temporary");
            egraph.FreeExternalFunction(first);
            Assert.Equal(baseline + 1, egraph.PanicFunctionCount);
            var second = egraph.NewPanic("temporary");
            Assert.Equal(baseline + 2, egraph.PanicFunctionCount);
            egraph.FreeExternalFunction(second);
            Assert.Equal(baseline + 1, egraph.PanicFunctionCount);
            Assert.Equal(keep, egraph.NewPanic("keep"));
            Assert.IsType<PanicFunction>(egraph.Database.GetExternalFunction(keep));
        }

        /// <summary>
        /// A relation <c>keyed(id) -&gt; i64</c> whose merge asserts the outputs are equal: a conflict records a
        /// failure.
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

        /// <summary>
        /// A failure recorded by a run that also stopped the context is thrown as <see cref="PanicException"/>; the
        /// stop does not hide it. A case's rule derives the contradiction and, in the same run, writes an assert-eq
        /// conflict.
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
        /// A failure the post-run rebuild records (an assert-eq conflict the run's union exposes) is thrown by that
        /// run, at the root and in a child, and is taken: the next run does not throw it again.
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

    }

}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Threading;

using Eggnog.Tests.Support;

using Xunit;

namespace Eggnog.Tests.Scopes
{

    /// <summary>
    /// A scope tree has no depth limit, as egglog's stack of pushes has none: chains far deeper than the 32 and 64
    /// layers a machine word holds write, run, read, extract, rebuild containers of classes, catch up and dispose at
    /// every level, nested cases included, and need no more stack than a shallow chain.
    /// </summary>
    public sealed class ScopeDepthTests
    {

        /// <summary>
        /// Probe deep_push.egg, which egglog at 5e655cae runs clean: <c>(datatype N (Z) (S N))</c>,
        /// <c>(function ns (i64) (Set N) :no-merge)</c>, <c>(relation seen (i64))</c> and the rule
        /// <c>((seen x) (&lt; x 1000)) ((seen (+ x 1000)))</c>; then 100 nested pushes, push <c>d</c> making
        /// <c>$td = (S $t(d-1))</c>, asserting <c>(seen d)</c>, setting <c>(ns d)</c> to <c>(set-of $td $t(d-10))</c> from
        /// 10 on, running once and checking <c>(seen d+1000)</c>. At the bottom <c>(union $t50 $t60)</c> makes the sets
        /// of <c>ns</c> 60 to 100 one element and <c>$t100</c> extract as 50 <c>S</c>s, and <c>(union $t20 $t30)</c> does
        /// the same from 30 on and makes it 20; <c>seen</c> has 200 rows. Popping undoes it all level by level, down to
        /// an empty root. Here the second union is written at depth 50 while its 50 descendants live, so the deepest
        /// scope sees it by catching up, as a writable ancestor's writes reach every descendant.
        /// </summary>
        [Fact]
        public void AHundredNestedScopesBehaveAsAHundredNestedPushes()
        {
            const int Depth = 100;
            var graph = new EGraph();
            graph.DefineSort<Node>("N");
            var z = graph.DefineConstructor<Node>("Z");
            var s = graph.DefineConstructor<Node, Node>("S");
            graph.Values.RegisterSet<Node>();
            var ns = graph.DefineFunction<long, Set<Node>>("ns");
            var seen = graph.DefineRelation<long>("seen");
            var rules = graph.DefineRuleset("rules");
            rules.DefineRule("lift", r =>
            {
                var x = r.Var<long>("x");
                r.When(seen.Of(x));
                r.Where(x, x => x < 1000);
                r.Then(seen.Of(Primitives.Add.Of(x, 1000L)));
            });
            var length = Primitives.Count<Node>();
            var setOf = Primitives.SetOf<Node>();

            var scopes = new List<Scope> { graph.Root };
            var terms = new List<Node> { graph.Root.Make(z.Of()) };
            for (var d = 1; d <= Depth; d++)
            {
                var scope = scopes[^1].CreateChild();
                scopes.Add(scope);
                terms.Add(scope.Make(s.Of(terms[d - 1])));
                scope.Assume(seen.Of((long)d));
                if (d >= 10)
                    scope.Assume(ns.Of((long)d).Is(setOf.Of(terms[d], terms[d - 10])));

                scope.Run(Schedule.Repeat(1, rules));
                Assert.True(scope.Check(seen.Of(d + 1000L)), $"depth {d}");
                Assert.False(scope.Check(seen.Of(d + 1001L)), $"depth {d}");
            }

            var deepest = scopes[^1];
            Assert.Equal(2 * Depth, deepest.CountRows(seen));
            Assert.True(deepest.Check(length.Of(ns.Of(70L)).Is(2L)));
            Assert.Equal(Nested(Depth), deepest.Extract(terms[Depth]).ToString());

            // A union at the bottom rebuilds the sets of classes the ancestors made.
            deepest.Union(terms[50], terms[60]);
            deepest.Run(Schedule.Repeat(1, rules));
            Assert.True(deepest.AreEqual(terms[55], terms[65]));
            Assert.True(deepest.Check(length.Of(ns.Of(70L)).Is(1L)));
            Assert.True(deepest.Check(length.Of(ns.Of(60L)).Is(1L)));
            Assert.True(deepest.Check(length.Of(ns.Of(59L)).Is(2L)));
            Assert.Equal(Nested(50), deepest.Extract(terms[Depth]).ToString());

            // A union written halfway up, which the deepest scope catches up with.
            Assert.True(scopes[50].Union(terms[20], terms[30]));
            Assert.True(deepest.AreEqual(terms[25], terms[35]));
            Assert.True(deepest.Check(length.Of(ns.Of(30L)).Is(1L)));
            Assert.True(deepest.Check(length.Of(ns.Of(29L)).Is(2L)));
            Assert.True(deepest.Check(length.Of(ns.Of(10L)).Is(2L)));
            Assert.Equal(Nested(20), deepest.Extract(terms[Depth]).ToString());
            Assert.Equal(2 * Depth, deepest.CountRows(seen));

            // So does a write at the root, and a rule run at the bottom over it.
            graph.Root.Assume(seen.Of(-5L));
            deepest.Run(Schedule.Repeat(1, rules));
            Assert.True(deepest.Check(seen.Of(995L)));
            Assert.False(scopes[99].Check(seen.Of(995L)));

            // Popping: each level is back to what it held, down to the root.
            for (var d = Depth; d >= 1; d--)
            {
                scopes[d].Dispose();
                Assert.True(scopes[d].IsDisposed);
                Assert.Empty(scopes[d - 1].Children);
                if (d == 51)
                {
                    Assert.Equal(2 * 50 + 1, scopes[50].CountRows(seen));
                    Assert.True(scopes[50].AreEqual(terms[40], terms[50]));
                    Assert.False(scopes[50].Check(seen.Of(995L)));
                }
            }

            Assert.False(graph.Root.Check(seen.Of(1L)));
            Assert.Equal(1, graph.Root.CountRows(seen));
            Assert.Equal(0, graph.Root.CountRows(ns));
        }

        /// <summary>
        /// Cases nest past 32 and 64 levels: each case of a 70-deep chain assumes its own fact and sees every
        /// ancestor's; lifting from the bottom up carries each one-case set's facts into its parent, down to the root.
        /// </summary>
        [Fact]
        public void CasesNestSeventyDeepAndLiftBackUp()
        {
            const int Depth = 70;
            var graph = new EGraph();
            var fact = graph.DefineRelation<long>("fact");
            var derived = graph.DefineRelation<long>("derived");
            var rules = graph.DefineRuleset("rules");
            rules.DefineRule("derive", r =>
            {
                var x = r.Var<long>("x");
                r.When(fact.Of(x));
                r.Then(derived.Of(x));
            });

            var parents = new List<Scope> { graph.Root.CreateChild() };
            var sets = new List<CaseSet>();
            for (var d = 1; d <= Depth; d++)
            {
                var cases = parents[^1].OpenCases();
                sets.Add(cases);
                var @case = cases.Add();
                Assert.True(@case.IsCase);
                @case.Assume(fact.Of((long)d));
                @case.Run(Schedule.Saturate(rules));
                parents.Add(@case);
            }

            var deepest = parents[^1];
            Assert.Equal(Depth, deepest.CountRows(derived));
            Assert.True(Enumerable.Range(1, Depth).All(d => deepest.Check(derived.Of((long)d))));

            for (var d = Depth; d >= 1; d--)
            {
                sets[d - 1].Lift();
                sets[d - 1].Dispose();
                Assert.Equal(Depth, parents[d - 1].CountRows(derived));
                Assert.True(parents[d - 1].Check(derived.Of((long)Depth)));
            }

            Assert.Equal(Depth, parents[0].CountRows(derived));
            parents[0].Dispose();
            Assert.Equal(0, graph.Root.CountRows(derived));
        }

        /// <summary>
        /// A run three hundred levels down, past the layers a frame entry's layer byte holds, joins rows of the deepest
        /// level with rows of levels near the root, through dense and sparse refinements alike.
        /// </summary>
        [Fact]
        public void ARunThreeHundredLevelsDownJoinsRowsOfEveryLevel()
        {
            const int Depth = 300;
            var graph = new EGraph();
            var edge = graph.DefineRelation<long, long>("edge");
            var path = graph.DefineRelation<long, long>("path");
            var rules = graph.DefineRuleset("rules");
            rules.DefineRule("base", r =>
            {
                var x = r.Var<long>("x");
                var y = r.Var<long>("y");
                r.When(edge.Of(x, y));
                r.Then(path.Of(x, y));
            });
            rules.DefineRule("step", r =>
            {
                var x = r.Var<long>("x");
                var y = r.Var<long>("y");
                var w = r.Var<long>("w");
                r.When(path.Of(x, y), edge.Of(y, w));
                r.Then(path.Of(x, w));
            });

            // Level d adds the edge d-1 → d, so the chain's edges are spread over every layer of the deepest view.
            var scope = graph.Root;
            for (var d = 1; d <= Depth; d++)
            {
                scope = scope.CreateChild();
                if (d % 10 == 0 || d > Depth - 3)
                    scope.Assume(edge.Of(d - 10L, (long)d));
            }

            var report = scope.Run(Schedule.Saturate(rules));
            Assert.True(report.Saturated);
            var edges = Enumerable.Range(1, Depth).Count(d => d % 10 == 0 || d > Depth - 3);
            Assert.Equal(edges, scope.CountRows(edge));
            Assert.True(scope.Check(path.Of(0L, 300L)));
            Assert.True(scope.Check(path.Of(280L, 300L)));
            Assert.True(scope.Check(path.Of(288L, 298L)));
            Assert.False(scope.Check(path.Of(0L, 299L)));
            Assert.False(scope.Parent!.Check(path.Of(0L, 300L)));

            for (var up = scope; up.Parent is { } parent; up = parent)
                up.Dispose();

            Assert.Empty(graph.Root.Children);
            Assert.Equal(0, graph.Root.CountRows(path));
        }

        /// <summary>
        /// Every walk over the levels of a chain is a loop, so depth costs no stack: on a thread with a 256 KB stack, which
        /// a recursion per level overflowed below 350 levels, a chain of 600 scopes is built, written at every 50th level
        /// and at the root, run and read at the bottom, frozen from the root down to its middle and disposed from the top,
        /// which the release of the deepest scope then carries up the whole chain.
        /// </summary>
        [Fact]
        public void ADeepChainNeedsNoMoreStackThanAShallowOne() => OnASmallStack(() =>
        {
            const int Depth = 600;
            var graph = new EGraph();
            var fact = graph.DefineRelation<long>("fact");
            var copy = graph.DefineRelation<long>("copy");
            var rules = graph.DefineRuleset("rules");
            rules.DefineRule("copy", r =>
            {
                var x = r.Var<long>("x");
                r.When(fact.Of(x));
                r.Then(copy.Of(x));
            });

            var scopes = new List<Scope> { graph.Root };
            for (var d = 1; d <= Depth; d++)
            {
                scopes.Add(scopes[^1].CreateChild());
                if (d % 50 == 0)
                    scopes[^1].Assume(fact.Of((long)d));
            }

            var deepest = scopes[^1];
            graph.Root.Assume(fact.Of(-1L));
            Assert.Equal(Depth / 50 + 1, deepest.CountRows(fact));
            deepest.Run(Schedule.Saturate(rules));
            Assert.Equal(Depth / 50 + 1, deepest.CountRows(copy));
            Assert.Equal(0, scopes[^2].CountRows(copy));

            for (var d = 0; d <= Depth / 2; d++)
                scopes[d].Freeze();

            deepest.Assume(fact.Of(-2L));
            Assert.True(deepest.Check(fact.Of(-1L)));
            Assert.Equal(Depth / 50 + 2, deepest.CountRows(fact));

            for (var d = 1; d < Depth; d++)
                scopes[d].Dispose();

            Assert.False(scopes[1].IsReleased);
            deepest.Dispose();
            Assert.True(scopes.Skip(1).All(scope => scope.IsReleased));
            Assert.Empty(graph.Root.Children);
        });

        /// <summary>
        /// Cases nested 200 deep on a small stack: a run of the outermost scope releases the cases under it level by
        /// level, a case stopped second from the top stops every case below it when they catch up, and the lift that drops
        /// it as vacuous disposes the whole nest under it.
        /// </summary>
        [Fact]
        public void ACaseStoppedNearTheTopStopsADeepNestBelowIt() => OnASmallStack(() =>
        {
            const int Depth = 200;
            var t = new NodeTheory();
            var parents = new List<Scope> { t.Root.CreateChild() };
            var sets = new List<CaseSet>();
            for (var d = 1; d <= Depth; d++)
            {
                var cases = parents[^1].OpenCases();
                sets.Add(cases);
                parents.Add(cases.Add());
            }

            var deepest = parents[^1];
            var a = deepest.Make(t.A.Of());
            deepest.Assume(t.Tag.Of(a));
            Assert.True(deepest.Check(t.Tag.Of(a)));
            parents[0].Run(Schedule.Saturate(t.Rules));
            Assert.False(deepest.IsDisposed);

            parents[2].Assume(t.Graph.Contradiction.Of());
            Assert.True(parents[2].IsContradictory);
            Assert.False(parents[1].IsContradictory);
            Assert.True(deepest.IsContradictory);
            Assert.Throws<ScopeContradictedException>(() => deepest.Check(t.Tag.Of(a)));
            Assert.Equal(RunStopReason.Contradiction, deepest.Run(Schedule.Saturate(t.Rules)).StopReason);
            Assert.True(parents[Depth / 2].IsContradictory);
            Assert.False(deepest.IsFaulted);

            Assert.True(sets[1].Lift().ParentContradicted);
            Assert.True(parents[2].IsDisposed);
            Assert.True(deepest.IsDisposed);
            Assert.True(parents[1].IsContradictory);
            Assert.False(parents[0].IsContradictory);
        });

        /// <summary>
        /// Runs <paramref name="body"/> on a thread with a 256 KB stack and rethrows what it throws.
        /// </summary>
        static void OnASmallStack(Action body)
        {
            Exception? failure = null;
            var thread = new Thread(() =>
            {
                try
                {
                    body();
                }
                catch (Exception e)
                {
                    failure = e;
                }
            }, 256 * 1024);
            thread.Start();
            thread.Join();
            if (failure is not null)
                ExceptionDispatchInfo.Capture(failure).Throw();
        }

        /// <summary>
        /// The term of <paramref name="depth"/> <c>S</c>s around <c>Z</c>, as extraction prints it.
        /// </summary>
        static string Nested(int depth) => string.Concat(Enumerable.Repeat("(S ", depth)) + "(Z)" + new string(')', depth);

    }

}

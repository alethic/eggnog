using System;
using System.Threading;

using Eggnog.Tests.Support;

using Xunit;

namespace Eggnog.Tests.Cases
{

    /// <summary>
    /// Host-driven lifts (§6.9.1, §6.9.4): what <see cref="CaseSet.Lift"/> writes into the parent, through the lift spec
    /// the rule layer builds from the graph's tables and their lattices' case joins.
    /// </summary>
    public sealed class LiftTests
    {

        [Fact]
        public void CaseJoinsComeFromTheLattices()
        {
            var graph = new EGraph();
            var dom = graph.DefineFunction<string, ValueSet<string>>("dom", ValueLattice.Intersect<string>());
            var lo = graph.DefineFunction<string, long>("lo", ValueLattice.Max<long>());
            var mostly = graph.DefineFunction<string, long>("mostly", new ValueLattice<long>("max-or-ten", Math.Max, (a, b) => Math.Max(10, Math.Min(a, b))));
            var exact = graph.DefineFunction<string, long>("exact");
            var root = graph.Root;

            using var cases = root.OpenCases();
            var first = cases.Add();
            first.Assume(dom.Of("x").Is(ValueSet.Of("a")), lo.Of("x").Is(5L), mostly.Of("x").Is(12L), exact.Of("x").Is(1L), exact.Of("y").Is(1L));
            var second = cases.Add();
            second.Assume(dom.Of("x").Is(ValueSet.Of("b")), lo.Of("x").Is(7L), mostly.Of("x").Is(3L), exact.Of("x").Is(1L), exact.Of("y").Is(2L));

            var lifted = cases.Lift();

            Assert.Equal(ValueSet.Of("a", "b"), root.Get(dom.Of("x")));   // ∩ merges, so the case join is ∪
            Assert.Equal(5L, root.Get(lo.Of("x")));                         // max merges, so the case join is min
            Assert.Equal(10L, root.Get(mostly.Of("x")));                    // the lattice's own case join
            Assert.Equal(1L, root.Get(exact.Of("x")));                      // no lattice: the cases agree
            Assert.False(root.TryGet(exact.Of("y"), out _));                // ...or nothing is lifted
            Assert.Equal(4, lifted.Values);
            Assert.Equal(0, lifted.Rows);
            Assert.Equal(2, lifted.LiveCases);
            Assert.Equal(0, lifted.VacuousCases);
            Assert.False(lifted.ParentContradicted);
            Assert.True(lifted.Changed);
            Assert.False(lifted.HitRoundLimit);
            Assert.Equal(0, lifted.Skipped);
            Assert.Contains("2 live", lifted.ToString(), StringComparison.Ordinal);
        }

        [Fact]
        public void RelationRowsLiftOnlyWhereEveryLiveCaseHoldsThem()
        {
            var graph = new EGraph();
            var present = graph.DefineRelation<string>("present");
            var root = graph.Root;

            using var cases = root.OpenCases();
            cases.Add().Assume(present.Of("a"), present.Of("b"));
            cases.Add().Assume(present.Of("a"), present.Of("c"));
            var lifted = cases.Lift();

            Assert.True(root.Check(present.Of("a")));
            Assert.False(root.Check(present.Of("b")));
            Assert.False(root.Check(present.Of("c")));
            Assert.Equal(1, lifted.Rows);                                   // a relation row, with its unit output
            Assert.Equal(0, lifted.Values);
            Assert.Equal(1L, root.CountRows(present));
        }

        [Fact]
        public void TablesThatDoNotLiftAreLeftOut()
        {
            var graph = new EGraph();
            var local = graph.DefineRelation<string>("local", new TableOptions { Lift = false });
            var analysis = graph.DefineRelation<string>("analysis", new TableOptions { Role = TableRole.Analysis });
            var lifting = graph.DefineRelation<string>("lifting", new TableOptions { Role = TableRole.Analysis, Lift = true });
            var root = graph.Root;
            Assert.False(local.Lift);
            Assert.False(analysis.Lift);

            using var cases = root.OpenCases();
            cases.Add().Assume(local.Of("a"), analysis.Of("a"), lifting.Of("a"));
            cases.Add().Assume(local.Of("a"), analysis.Of("a"), lifting.Of("a"));
            cases.Lift();

            Assert.False(root.Check(local.Of("a")));
            Assert.False(root.Check(analysis.Of("a")));
            Assert.True(root.Check(lifting.Of("a")));
        }

        [Fact]
        public void TermsAndEqualitiesEveryCaseHoldsAreLifted()
        {
            var t = new NodeTheory();
            var root = t.Root;
            var a = root.Make(t.A.Of());
            var b = root.Make(t.B.Of());

            using var cases = root.OpenCases();
            foreach (var @case in new[] { cases.Add(), cases.Add() })
            {
                Assert.True(@case.Union(a, b));
                @case.Make(t.Wrap.Of(a));
            }

            var lifted = cases.Lift();

            Assert.True(root.AreEqual(a, b));
            Assert.True(root.TryGet(t.Wrap.Of(b), out _));                 // the term, over the lifted equality
            Assert.True(lifted.Unions >= 1);
            Assert.True(lifted.Rows + lifted.Terms >= 1);
            Assert.True(lifted.Changed);
        }

        [Fact]
        public void TheContradictionAndMirrorTablesAreNeverLiftedAsRows()
        {
            var t = new ScopeTheory();
            var copies = new OuterCopies(t);
            var parent = t.Root.CreateChild();

            using var cases = parent.OpenCases();
            var vacuous = cases.Add(t.Outer);
            vacuous.Assume(t.Fact.Of("a"), t.Graph.Contradiction.Of());
            Assert.True(vacuous.IsContradictory);
            var live = cases.Add(t.Outer);
            live.Assume(t.Fact.Of("a"));

            // The live case syncs its mirror of fact under its own tag (it is tagged outer) before its run reads it.
            live.Run(Schedule.Saturate(t.Rules));
            Assert.True(live.Check(copies.Facts.Of("a")));
            var mirror = t.Graph.Mirrors.Get(t.Fact, t.Outer).Function;
            Assert.True(live.Engine.TableSize(mirror) > 0);

            var lifted = cases.Lift();

            Assert.Equal(1, lifted.LiveCases);
            Assert.Equal(1, lifted.VacuousCases);
            Assert.False(lifted.ParentContradicted);
            Assert.False(parent.IsContradictory);                           // one case is vacuous, not the parent
            Assert.True(parent.Check(t.Fact.Of("a")));
            Assert.Equal(0L, parent.Engine.TableSize(mirror));                // mirrors are kept by syncs, never lifted
        }

        [Fact]
        public void VacuousCasesAreDisposedAfterTheLiftAndLeaveTheSet()
        {
            var t = new ScopeTheory();
            var parent = t.Root.CreateChild();
            using var cases = parent.OpenCases();
            var vacuous = cases.Add();
            vacuous.Assume(t.Graph.Contradiction.Of());
            var live = cases.Add();
            live.Assume(t.Fact.Of("a"));
            Assert.Equal(2, parent.Children.Count);

            cases.Lift();

            Assert.True(vacuous.IsDisposed);
            Assert.True(vacuous.Engine.IsDisposed);
            Assert.Same(live, Assert.Single(cases.Cases));
            Assert.Same(live, Assert.Single(parent.Children));
            Assert.Throws<ObjectDisposedException>(() => vacuous.IsContradictory);
        }

        [Fact]
        public void EveryCaseVacuousContradictsTheParent()
        {
            var t = new ScopeTheory();
            var parent = t.Root.CreateChild();
            using var cases = parent.OpenCases();
            cases.Add().Assume(t.Graph.Contradiction.Of());
            cases.Add().Assume(t.Graph.Contradiction.Of());

            var lifted = cases.Lift();

            Assert.True(lifted.ParentContradicted);
            Assert.Equal(2, lifted.VacuousCases);
            Assert.Equal(0, lifted.LiveCases);
            Assert.True(lifted.Changed);
            Assert.True(parent.IsContradictory);
            Assert.Empty(cases.Cases);
            Assert.Empty(parent.Children);

            // The set remembers its vacuous cases: lifting again still contradicts, and changes nothing more.
            var again = cases.Lift();
            Assert.True(again.ParentContradicted);
            Assert.False(again.Changed);
        }

        [Fact]
        public void ALiftWithNoCaseThrows()
        {
            var t = new ScopeTheory();
            var parent = t.Root.CreateChild();
            using var cases = parent.OpenCases();

            var refused = Assert.Throws<InvalidOperationException>(() => cases.Lift());
            Assert.StartsWith("A lift needs at least one case", refused.Message, StringComparison.Ordinal);
            Assert.False(parent.IsContradictory);
        }

        [Fact]
        public void CancellationIsCheckedBetweenRounds()
        {
            var t = new ScopeTheory();
            var parent = t.Root.CreateChild();
            using var cases = parent.OpenCases();
            cases.Add().Assume(t.Fact.Of("a"));
            cases.Add().Assume(t.Fact.Of("a"));
            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            var version = parent.Version;

            Assert.Throws<OperationCanceledException>(() => cases.Lift(null, cancelled.Token));

            Assert.Equal(version, parent.Version);
            Assert.False(parent.Check(t.Fact.Of("a")));
            Assert.False(parent.IsFaulted);
            Assert.True(cases.Lift().Changed);
            Assert.True(parent.Check(t.Fact.Of("a")));
        }

        [Fact]
        public void ALiftMayBeRepeatedAndLiftsWhatTheCasesLearnedSince()
        {
            var t = new ScopeTheory();
            var parent = t.Root.CreateChild();
            using var cases = parent.OpenCases();
            var first = cases.Add();
            var second = cases.Add();
            first.Assume(t.Fact.Of("a"));
            second.Assume(t.Fact.Of("a"));
            Assert.True(cases.Lift().Changed);

            var unchanged = cases.Lift();
            Assert.False(unchanged.Changed);
            Assert.Equal(0, unchanged.Rounds);

            first.Assume(t.Fact.Of("b"));
            second.Assume(t.Fact.Of("b"));
            Assert.True(cases.Lift(new LiftOptions { RoundLimit = 1, CandidateLimit = 1 }).Changed);
            Assert.True(parent.Check(t.Fact.Of("b")));
        }

        [Fact]
        public void ACaseDisposedDirectlyStopsTheSetFromLifting()
        {
            var t = new ScopeTheory();
            var parent = t.Root.CreateChild();
            var cases = parent.OpenCases();
            var first = cases.Add();
            var second = cases.Add();
            first.Assume(t.Fact.Of("a"));
            second.Assume(t.Fact.Of("a"));

            // Without the second case, the first alone does not cover the parent.
            second.Dispose();
            Assert.True(second.IsDisposed);
            Assert.Same(first, Assert.Single(cases.Cases));
            Assert.Throws<InvalidOperationException>(() => cases.Lift());
            Assert.False(parent.Check(t.Fact.Of("a")));

            // The set owns its cases: the disposed case is released with the set.
            Assert.False(second.Engine.IsDisposed);
            cases.Dispose();
            cases.Dispose();
            Assert.True(second.Engine.IsDisposed);
            Assert.True(first.IsDisposed);
            Assert.Empty(parent.Children);
            Assert.Throws<ObjectDisposedException>(() => cases.Add());
            Assert.Throws<ObjectDisposedException>(() => cases.Lift());
        }

        [Fact]
        public void DisposingTheSetWaitsForACasesChild()
        {
            var t = new ScopeTheory();
            var parent = t.Root.CreateChild();
            var cases = parent.OpenCases();
            var @case = cases.Add();
            @case.Assume(t.Fact.Of("a"));
            var child = @case.CreateChild();

            // Disposal never throws (D40): the case is disposed at once, and released with its child.
            cases.Dispose();
            Assert.True(@case.IsDisposed);
            Assert.False(@case.Engine.IsDisposed);
            Assert.True(child.Check(t.Fact.Of("a")));
            child.Dispose();
            Assert.True(@case.Engine.IsDisposed);
            Assert.Empty(parent.Children);
        }

        [Fact]
        public void LiftOptionsRefuseLimitsBelowOne()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new LiftOptions { RoundLimit = 0 });
            Assert.Throws<ArgumentOutOfRangeException>(() => new LiftOptions { CandidateLimit = 0 });
            Assert.Equal(16, LiftOptions.Default.RoundLimit);
            Assert.Equal(64, LiftOptions.Default.CandidateLimit);
        }

        [Fact]
        public void ACaseJoinThatThrowsFailsTheLiftWithoutWriting()
        {
            var graph = new EGraph();
            var broken = graph.DefineFunction<string, long>("broken", new ValueLattice<long>("broken", Math.Max, (a, b) => throw new InvalidOperationException("no join")));
            var root = graph.Root;
            using var cases = root.OpenCases();
            cases.Add().Assume(broken.Of("x").Is(1L));
            cases.Add().Assume(broken.Of("x").Is(2L));

            var failure = Assert.Throws<RuleEvaluationException>(() => cases.Lift());

            Assert.Null(failure.RuleName);
            Assert.Contains("case join", failure.Message, StringComparison.Ordinal);
            Assert.IsType<InvalidOperationException>(failure.InnerException);
            Assert.False(root.TryGet(broken.Of("x"), out _));
            Assert.False(root.IsFaulted);
        }

    }

}

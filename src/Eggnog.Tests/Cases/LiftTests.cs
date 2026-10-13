using System;
using System.Threading;
using System.Threading.Tasks;

using Eggnog.Tests.Support;

using Xunit;

namespace Eggnog.Tests.Cases
{

    /// <summary>
    /// Host-driven lifts: what <see cref="CaseSet.Lift"/> writes into the parent, through the lift spec the rule layer
    /// builds from the graph's tables and their lattices' case joins, and the life of a <see cref="CaseSet"/> and its
    /// cases.
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

            // Disposal never throws: the case is disposed at once, and released with its child.
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

        /// <summary>
        /// <see cref="CaseSet.Dispose"/> may be called from any thread, never throws, and disposes every case, after
        /// which the parent's release can proceed. A case that <c>Add</c> makes while another thread disposes the set
        /// does not outlive the set: here <c>Add</c> is held inside the parent's catch-up (a lattice merge of a value
        /// its writable ancestor wrote since) while the set is disposed on another thread, and either <c>Add</c>
        /// refuses or the case it made is disposed with the set.
        /// </summary>
        [Fact]
        public void ACaseAddedWhileTheSetIsDisposedOnAnotherThreadIsDisposedWithIt()
        {
            using var entered = new ManualResetEventSlim();
            using var proceed = new ManualResetEventSlim();
            var block = 0;
            var graph = new EGraph();
            var max = new ValueLattice<long>("blocking max", (a, b) =>
            {
                if (Interlocked.Exchange(ref block, 0) == 1)
                {
                    entered.Set();
                    proceed.Wait(TimeSpan.FromSeconds(30));
                }

                return Math.Max(a, b);
            }, Math.Min);
            var f = graph.DefineFunction<string, long>("f", max);
            graph.Root.Freeze();
            var ancestor = graph.Root.CreateChild();
            var parent = ancestor.CreateChild();
            parent.Assume(f.Of("x").Is(1L));
            var cases = parent.OpenCases();

            // The ancestor's write reaches the parent at its next call: Add's catch-up merges 1 with 2.
            ancestor.Assume(f.Of("x").Is(2L));
            Volatile.Write(ref block, 1);
            var disposer = Task.Run(() =>
            {
                if (entered.Wait(TimeSpan.FromSeconds(30)))
                    cases.Dispose();

                proceed.Set();
            });

            Scope? added = null;
            Exception? refused = null;
            try
            {
                added = cases.Add();
            }
            catch (ObjectDisposedException exception)
            {
                refused = exception;
            }

            disposer.Wait();
            Assert.True(entered.IsSet, "the catch-up ran the lattice merge");

            // Either Add refuses (the set was disposed under it), or the case it made is disposed with the set.
            Assert.True(refused is not null || added is not null);
            var listed = cases.Cases.Count;
            var alive = added is { IsDisposed: false };

            // The parent's release, which the open set deferred, proceeds once the parent is disposed, even when the
            // stray case is disposed directly as well.
            added?.Dispose();
            parent.Dispose();
            Assert.True(parent.Engine.IsDisposed, "the parent is never released: a case of the disposed set still holds it");
            Assert.Equal(0, listed);
            Assert.False(alive, "the set was disposed, and a case of it is alive");
        }

        /// <summary>
        /// <see cref="RunOptions.Lift"/> refuses <c>null</c>, and defaults to <see cref="LiftOptions.Default"/>.
        /// </summary>
        [Fact]
        public void RunOptionsRefuseANullLift()
        {
            Assert.Throws<ArgumentNullException>(() => new RunOptions { Lift = null! });
            Assert.Same(LiftOptions.Default, RunOptions.Default.Lift);
        }

        /// <summary>
        /// A table defined after a first lift lifts too, with its lattice's case join: definitions are never sealed,
        /// and the lift spec cached on the graph is rebuilt when a table is defined.
        /// </summary>
        [Fact]
        public void TablesDefinedAfterALiftAreLiftedToo()
        {
            var graph = new EGraph();
            var first = graph.DefineRelation<string>("first");
            var root = graph.Root;
            using (var cases = root.OpenCases())
            {
                cases.Add().Assume(first.Of("a"));
                cases.Add().Assume(first.Of("a"));
                Assert.Equal(1, cases.Lift().Rows);
            }

            var later = graph.DefineRelation<string>("later");
            var dom = graph.DefineFunction<string, ValueSet<string>>("dom", ValueLattice.Intersect<string>());
            using (var cases = root.OpenCases())
            {
                cases.Add().Assume(later.Of("b"), dom.Of("k").Is(ValueSet.Of("x")));
                cases.Add().Assume(later.Of("b"), dom.Of("k").Is(ValueSet.Of("y")));
                var lifted = cases.Lift();
                Assert.Equal(1, lifted.Rows);
                Assert.Equal(1, lifted.Values);
            }

            Assert.True(root.Check(later.Of("b")));
            Assert.Equal(ValueSet.Of("x", "y"), root.Get(dom.Of("k")));
        }

        /// <summary>
        /// A case disposed directly stops its set from lifting unless it is vacuous once caught up with its parent:
        /// once the parent's contradiction reaches it, the disposed case does not stop the lift, which finds every case
        /// vacuous and contradicts the parent.
        /// </summary>
        [Fact]
        public void ACaseDisposedDirectlyThatIsVacuousDoesNotStopTheLift()
        {
            var t = new ScopeTheory();
            var parent = t.Root.CreateChild();
            using var cases = parent.OpenCases();
            var kept = cases.Add();
            var dropped = cases.Add();
            kept.Assume(t.Fact.Of("a"));
            dropped.Assume(t.Fact.Of("a"));
            dropped.Dispose();
            Assert.Throws<InvalidOperationException>(() => cases.Lift());

            parent.Assume(t.Graph.Contradiction.Of());
            var lifted = cases.Lift();

            Assert.True(lifted.ParentContradicted);
            Assert.True(parent.IsContradictory);
            Assert.Empty(cases.Cases);
        }

        /// <summary>
        /// A host-driven set with a faulted case: the lift reads a faulted case as any other, and lifts only what every
        /// live case holds, never what the failed iteration would have derived; the parent is not faulted.
        /// </summary>
        [Fact]
        public void ALiftOverAFaultedCaseLiftsOnlyWhatEveryCaseHolds()
        {
            var t = new ScopeTheory();
            t.Rules.DefineRule("boom", r =>
            {
                var s = r.Var<string>("s");
                r.When(t.Seen.Of(s));
                r.Where(s, _ => throw new InvalidOperationException("boom"));
                r.Then(t.Fact.Of("never"));
            });

            var parent = t.Root.CreateChild();
            using var cases = parent.OpenCases();
            var failing = cases.Add();
            var other = cases.Add();
            failing.Assume(t.Fact.Of("x"), t.Seen.Of("s"));
            other.Assume(t.Fact.Of("x"), t.Fact.Of("never"));
            Assert.Throws<RuleEvaluationException>(() => failing.Run(Schedule.Saturate(t.Rules)));
            Assert.True(failing.IsFaulted);

            var lifted = cases.Lift();

            Assert.Equal(2, lifted.LiveCases);
            Assert.True(parent.Check(t.Fact.Of("x")));
            Assert.False(parent.Check(t.Fact.Of("never")));
            Assert.False(parent.IsFaulted);
        }

        /// <summary>
        /// A case that went vacuous only because its parent later became contradictory is dropped, and with every case
        /// vacuous the parent stays contradictory; nothing else is lifted.
        /// </summary>
        [Fact]
        public void ACaseMadeVacuousByItsParentIsDropped()
        {
            var t = new ScopeTheory();
            var parent = t.Root.CreateChild();
            using var cases = parent.OpenCases();
            var a = cases.Add();
            var b = cases.Add();
            a.Assume(t.Fact.Of("a"), t.Fact.Of("both"));
            b.Assume(t.Fact.Of("b"), t.Fact.Of("both"));
            parent.Assume(t.Graph.Contradiction.Of());

            var lifted = cases.Lift();

            Assert.True(lifted.ParentContradicted);
            Assert.Equal(0, lifted.LiveCases);
            Assert.Empty(cases.Cases);
            Assert.False(parent.Check(t.Fact.Of("both")));
            Assert.True(parent.IsContradictory);
        }

    }

}

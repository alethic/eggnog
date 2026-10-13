using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;

using Xunit;

using static Eggnog.Relations.Tests.BridgeTestUtil;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// The lift: equalities through the meet, facts through the seed's own rows and the winner probe, lattice values
    /// through case joins, terms bottom-up, vacuous cases, the contracts of the table kinds, limits, the memo,
    /// cancellation between rounds, and the lift reads a root refuses.
    /// </summary>
    public class LifterTests
    {

        /// <summary>
        /// A graph whose contradiction function is designated, with <paramref name="count"/> root ids.
        /// </summary>
        static (ContextGraph Graph, Value[] Ids) Setup(int count)
        {
            var g = new ContextGraph();
            g.EGraph.DesignateContradiction(g.Bottom);
            var ids = Enumerable.Range(0, count).Select(_ => g.Root.FreshId()).ToArray();
            return (g, ids);
        }

        /// <summary>
        /// The relations of the context graph as relations, its constructors as constructors, and <c>low</c> joined by
        /// the maximum (the case join of a table merged by the minimum).
        /// </summary>
        static LiftSpec Spec(ContextGraph g) => new(
            new LiftTable(g.Edge, LiftTableKind.Relation),
            new LiftTable(g.Path, LiftTableKind.Relation),
            new LiftTable(g.Mark, LiftTableKind.Relation),
            new LiftTable(g.Bottom, LiftTableKind.Relation),
            new LiftTable(g.Num, LiftTableKind.Constructor),
            new LiftTable(g.Add, LiftTableKind.Constructor),
            new LiftTable(g.Low, LiftTableKind.Function, (a, b) => Join(g, a, b)));

        static uint Join(ContextGraph g, uint a, uint b)
        {
            var values = g.EGraph.BaseValues;
            var x = values.Unwrap<Int64Codec, long>(new Value(a));
            var y = values.Unwrap<Int64Codec, long>(new Value(b));
            return values.Get<Int64Codec, long>(Math.Max(x, y)).Rep;
        }

        static void CheckAll(EGraphContext parent, CaseSet set)
        {
            parent.CheckInvariants();
            foreach (var @case in set.Cases)
                @case.CheckInvariants();
        }

        /// <summary>
        /// A fact that holds in a case only through an ancestor row naming a class's winner is found by the winner
        /// probe. Case 1 merges b into a (a wins, so the parent's <c>mark(a)</c> is not copied, and <c>mark(b)</c>
        /// holds only through it); case 2 asserts <c>mark(b)</c> without that equality. Case 1 is the seed (both have
        /// one group, and ties go to the first), so its own rows are empty and only the winner probe finds the fact.
        /// </summary>
        [Fact]
        public void G1bFactThroughWinnerIsLifted()
        {
            var (g, ids) = Setup(4);
            var (a, b, c, d) = (ids[0], ids[1], ids[2], ids[3]);
            g.Assert(g.Root, g.Mark, a);
            g.Root.FlushUpdates();

            using var set = new CaseSet(g.Root);
            var one = set.AddCase();
            one.StageUnion(a, b);
            var two = set.AddCase();
            g.Assert(two, g.Mark, b);
            two.StageUnion(c, d);
            one.EnsureClean();
            two.EnsureClean();

            // The seed holds no mark row of its own; the only row naming its group root is the parent's.
            var own = new RowCountSink();
            one.ScanOwn(g.Mark, ref own);
            Assert.Equal(0, own.Count);
            var winner = new RowCountSink();
            one.ScanAncestorsMentioning(g.Mark, a.Rep, ref winner);
            Assert.Equal(1, winner.Count);
            var loser = new RowCountSink();
            one.ScanAncestorsMentioning(g.Mark, b.Rep, ref loser);
            Assert.Equal(0, loser.Count);

            var report = set.Lift(Spec(g), LiftOptions.Default);
            Assert.True(report.Changed);
            Assert.Equal(1, report.Rows);
            Assert.Equal(0, report.Unions);
            Assert.True(g.Root.ContainsKey(g.Mark, [b]));
            Assert.NotEqual(g.Root.Find(a), g.Root.Find(b));
            CheckAll(g.Root, set);
        }

        /// <summary>
        /// A boolean split: b is true in one case and false in the other. What follows in both (b reaches c) is lifted,
        /// through the parent's rows about the winners; b's value is not.
        /// </summary>
        [Fact]
        public void ABooleanSplitLiftsWhatBothBranchesDerive()
        {
            var (g, ids) = Setup(4);
            var (t, f, c, b) = (ids[0], ids[1], ids[2], ids[3]);
            g.Assert(g.Root, g.Edge, t, c);
            g.Assert(g.Root, g.Edge, f, c);
            g.Root.FlushUpdates();
            var copy = g.EdgeToPath();

            using var set = new CaseSet(g.Root);
            var yes = set.AddCase();
            yes.StageUnion(b, t);
            yes.RunRules(copy);
            var no = set.AddCase();
            no.StageUnion(b, f);
            no.RunRules(copy);

            var report = set.Lift(Spec(g), LiftOptions.Default);
            Assert.True(report.Changed);
            Assert.True(g.Root.ContainsKey(g.Edge, [b, c]));
            Assert.True(g.Root.ContainsKey(g.Path, [b, c]));
            Assert.NotEqual(g.Root.Find(b), g.Root.Find(t));
            Assert.NotEqual(g.Root.Find(b), g.Root.Find(f));
            Assert.Equal(2, report.LiveCases);
            CheckAll(g.Root, set);
        }

        [Fact]
        public void AThreeCaseMeetLiftsOnlyTheCommonEqualities()
        {
            var (g, ids) = Setup(6);
            using var set = new CaseSet(g.Root);
            var one = set.AddCase();
            one.StageUnion(ids[0], ids[1]);
            one.StageUnion(ids[1], ids[2]);
            one.StageUnion(ids[3], ids[4]);
            var two = set.AddCase();
            two.StageUnion(ids[0], ids[2]);
            two.StageUnion(ids[1], ids[2]);
            two.StageUnion(ids[4], ids[5]);
            var three = set.AddCase();
            three.StageUnion(ids[2], ids[1]);
            three.StageUnion(ids[1], ids[0]);
            three.StageUnion(ids[3], ids[4]);

            var report = set.Lift(LiftSpec.Empty, LiftOptions.Default);
            Assert.Equal(2, report.Unions);
            Assert.Equal(g.Root.Find(ids[0]), g.Root.Find(ids[1]));
            Assert.Equal(g.Root.Find(ids[0]), g.Root.Find(ids[2]));
            Assert.NotEqual(g.Root.Find(ids[3]), g.Root.Find(ids[4]));
            Assert.NotEqual(g.Root.Find(ids[4]), g.Root.Find(ids[5]));
            CheckAll(g.Root, set);
        }

        /// <summary>
        /// With one live case, what it merged is lifted class by class: two classes stay two classes (the meet over no
        /// other case must not alias its groups).
        /// </summary>
        [Fact]
        public void NoAliasingWithOneLiveCase()
        {
            var (g, ids) = Setup(4);
            using var set = new CaseSet(g.Root);
            var only = set.AddCase();
            only.StageUnion(ids[0], ids[1]);
            only.StageUnion(ids[2], ids[3]);
            var vacuous = set.AddCase();
            vacuous.StageRow(g.Bottom, [g.Unit]);

            var report = set.Lift(LiftSpec.Empty, LiftOptions.Default);
            Assert.Equal(1, report.LiveCases);
            Assert.Equal(1, report.VacuousCases);
            Assert.Equal(2, report.Unions);
            Assert.Equal(g.Root.Find(ids[0]), g.Root.Find(ids[1]));
            Assert.Equal(g.Root.Find(ids[2]), g.Root.Find(ids[3]));
            Assert.NotEqual(g.Root.Find(ids[0]), g.Root.Find(ids[2]));
        }

        /// <summary>
        /// A vacuous case is left out of the meet and the facts, so what the live cases share is lifted although the
        /// vacuous one never had it; <see cref="CaseSet.DisposeVacuous"/> then drops it.
        /// </summary>
        [Fact]
        public void VacuousCasesAreDropped()
        {
            var (g, ids) = Setup(3);
            using var set = new CaseSet(g.Root);
            foreach (var _ in Enumerable.Range(0, 2))
            {
                var live = set.AddCase();
                live.StageUnion(ids[0], ids[1]);
                g.Assert(live, g.Mark, ids[2]);
            }

            var dead = set.AddCase();
            g.Assert(dead, g.Mark, ids[0]);
            dead.StageRow(g.Bottom, [g.Unit]);
            dead.FlushUpdates();
            Assert.True(dead.IsStopped);

            var report = set.Lift(Spec(g), LiftOptions.Default);
            Assert.Equal(2, report.LiveCases);
            Assert.Equal(1, report.VacuousCases);
            Assert.False(report.ParentContradicted);
            Assert.Equal(g.Root.Find(ids[0]), g.Root.Find(ids[1]));
            Assert.True(g.Root.ContainsKey(g.Mark, [ids[2]]));
            Assert.False(g.Root.ContainsKey(g.Mark, [ids[0]]));
            Assert.False(g.Root.IsContradictory);

            Assert.Equal(1, set.DisposeVacuous());
            Assert.True(dead.IsDisposed);
            Assert.Equal(2, set.Cases.Count);
            Assert.Equal(1, set.VacuousCount);
        }

        /// <summary>
        /// When every case is vacuous the parent is contradictory: the contradiction is written into it, and a parent
        /// that is itself a case stops.
        /// </summary>
        [Fact]
        public void AllVacuousContradictsTheParent()
        {
            var (g, ids) = Setup(2);
            using var set = new CaseSet(g.Root);
            foreach (var _ in Enumerable.Range(0, 2))
                set.AddCase().StageRow(g.Bottom, [g.Unit]);

            var report = set.Lift(Spec(g), LiftOptions.Default);
            Assert.True(report.ParentContradicted);
            Assert.Equal(0, report.LiveCases);
            Assert.Equal(2, report.VacuousCases);
            Assert.True(report.Changed);
            Assert.True(g.Root.IsContradictory);
            Assert.False(g.Root.IsStopped);

            // A parent that stops on contradictions stops.
            var (h, _) = Setup(1);
            h.Root.Freeze();
            using var scope = h.Root.CreateChild(ContextOptions.Case);
            using var inner = new CaseSet(scope);
            inner.AddCase().StageRow(h.Bottom, [h.Unit]);
            var stopped = inner.Lift(Spec(h), LiftOptions.Default);
            Assert.True(stopped.ParentContradicted);
            Assert.True(scope.IsStopped);
            inner.Dispose();
        }

        /// <summary>
        /// Cases a schedule found vacuous and disposed (<see cref="CaseSet.RunEach"/>) still count: when none is left,
        /// the lift contradicts the parent.
        /// </summary>
        [Fact]
        public void CasesDisposedAsVacuousStillContradictTheParent()
        {
            var (g, _) = Setup(1);
            using var set = new CaseSet(g.Root);
            set.AddCase().StageRow(g.Bottom, [g.Unit]);
            set.AddCase().StageRow(g.Bottom, [g.Unit]);
            Assert.False(set.RunEach(new RecordingSchedule(), 0, false));
            Assert.Empty(set.Cases);
            Assert.Equal(2, set.VacuousCount);
            var report = set.Lift(Spec(g), LiftOptions.Default);
            Assert.True(report.ParentContradicted);
            Assert.Equal(2, report.VacuousCases);
            Assert.True(g.Root.IsContradictory);
        }

        [Fact]
        public void ALiftWithNoCaseIsRefused()
        {
            var (g, _) = Setup(1);
            using var set = new CaseSet(g.Root);
            Assert.Throws<InvalidOperationException>(() => set.Lift(Spec(g), LiftOptions.Default));
        }

        /// <summary>
        /// Lattice values: the case join (here the maximum, for a table merged by the minimum) combines differing
        /// values; without a join, only an agreed value is lifted.
        /// </summary>
        [Fact]
        public void ALatticeValueIsLiftedThroughTheCaseJoin()
        {
            var (g, ids) = Setup(3);
            g.Root.StageRow(g.Low, [ids[0], g.Int(9)]);
            g.Root.FlushUpdates();
            using var set = new CaseSet(g.Root);
            var one = set.AddCase();
            one.StageRow(g.Low, [ids[0], g.Int(3)]);
            one.StageRow(g.Low, [ids[1], g.Int(4)]);
            one.StageRow(g.Low, [ids[2], g.Int(6)]);
            var two = set.AddCase();
            two.StageRow(g.Low, [ids[0], g.Int(5)]);
            two.StageRow(g.Low, [ids[1], g.Int(4)]);

            var report = set.Lift(Spec(g), LiftOptions.Default);
            Assert.Equal(2, report.Values);
            Assert.Equal(g.Int(5), g.Root.LookupId(g.Low, [ids[0]]));
            Assert.Equal(g.Int(4), g.Root.LookupId(g.Low, [ids[1]]));
            Assert.Null(g.Root.LookupId(g.Low, [ids[2]]));

            // Without a join, values the cases disagree on are not lifted.
            var (h, hid) = Setup(1);
            using var plain = new CaseSet(h.Root);
            plain.AddCase().StageRow(h.Low, [hid[0], h.Int(3)]);
            plain.AddCase().StageRow(h.Low, [hid[0], h.Int(5)]);
            var none = plain.Lift(new LiftSpec(new LiftTable(h.Low, LiftTableKind.Function)), LiftOptions.Default);
            Assert.Equal(0, none.Values);
            Assert.Null(h.Root.LookupId(h.Low, [hid[0]]));
        }

        /// <summary>
        /// Terms bottom-up: each case builds <c>add(num(1), num(2))</c> from local ids. Round 1 lifts the <c>num</c>
        /// terms (their keys are base values); once the cases see the parent's terms, their local ids stand for parent
        /// ids and round 2 lifts the <c>add</c> term over them.
        /// </summary>
        [Fact]
        public void TermsAreLiftedBottomUp()
        {
            var (g, _) = Setup(0);
            using var set = new CaseSet(g.Root);
            for (var i = 0; i < 2; i++)
            {
                var @case = set.AddCase();
                var one = @case.AddTerm(g.Num, [g.Int(1)]);
                var two = @case.AddTerm(g.Num, [g.Int(2)]);
                @case.AddTerm(g.Add, [one, two]);
                Assert.True(@case.IsLocalId(one));
            }

            var report = set.Lift(Spec(g), LiftOptions.Default);
            Assert.True(report.Rounds >= 2);
            Assert.Equal(3, report.Terms);
            var pOne = g.Root.LookupId(g.Num, [g.Int(1)]);
            var pTwo = g.Root.LookupId(g.Num, [g.Int(2)]);
            Assert.NotNull(pOne);
            Assert.NotNull(pTwo);
            Assert.NotNull(g.Root.LookupId(g.Add, [pOne!.Value, pTwo!.Value]));
            CheckAll(g.Root, set);

            // With one round, only the leaves are lifted.
            var (h, _) = Setup(0);
            using var limited = new CaseSet(h.Root);
            for (var i = 0; i < 2; i++)
            {
                var @case = limited.AddCase();
                var one = @case.AddTerm(h.Num, [h.Int(1)]);
                @case.AddTerm(h.Add, [one, one]);
            }

            var cut = limited.Lift(Spec(h), new LiftOptions { RoundLimit = 1 });
            Assert.True(cut.HitRoundLimit);
            Assert.Equal(1, cut.Rounds);
            Assert.NotNull(h.Root.LookupId(h.Num, [h.Int(1)]));
            Assert.Equal(0, h.Root.TableSize(h.Add));

            // The next lift is not skipped by the memo, and finishes the job.
            var rest = limited.Lift(Spec(h), LiftOptions.Default);
            Assert.True(rest.Changed);
            Assert.Equal(1, h.Root.TableSize(h.Add));
        }

        /// <summary>
        /// A constructor term whose output every case equates with one parent id is written with that id (a row), not
        /// with a fresh one.
        /// </summary>
        [Fact]
        public void AConstructorWithACommonOutputIsWrittenAsARow()
        {
            var (g, ids) = Setup(1);
            using var set = new CaseSet(g.Root);
            for (var i = 0; i < 2; i++)
            {
                var @case = set.AddCase();
                var term = @case.AddTerm(g.Num, [g.Int(7)]);
                @case.StageUnion(term, ids[0]);
            }

            var report = set.Lift(Spec(g), LiftOptions.Default);
            Assert.Equal(1, report.Rows);
            Assert.Equal(0, report.Terms);
            Assert.Equal(g.Root.Find(ids[0]), g.Root.Find(g.Root.LookupId(g.Num, [g.Int(7)])!.Value));
        }

        /// <summary>
        /// The candidate limit skips a source row standing for too many parent keys, and counts them; the round limit
        /// cuts a lift short (see <see cref="TermsAreLiftedBottomUp"/>).
        /// </summary>
        [Fact]
        public void TheCandidateLimitSkipsWideRows()
        {
            var (g, ids) = Setup(4);
            using var set = new CaseSet(g.Root);
            for (var i = 0; i < 2; i++)
            {
                var @case = set.AddCase();
                @case.StageUnion(ids[0], ids[1]);
                @case.StageUnion(ids[1], ids[2]);
                g.Assert(@case, g.Edge, ids[2], ids[3]);
                g.Assert(@case, g.Path, ids[2], ids[2]);
            }

            // edge(c, d) stands for 3 parent keys, path(c, c) for 9.
            var report = set.Lift(Spec(g), new LiftOptions { CandidateLimit = 4 });
            Assert.Equal(9, report.Skipped);
            Assert.True(g.Root.ContainsKey(g.Edge, [ids[0], ids[3]]));

            // The equalities were lifted in the same round; once the parent holds them, the skipped row stands for one
            // parent key, which the next round lifts.
            Assert.Equal(g.Root.Find(ids[0]), g.Root.Find(ids[2]));
            Assert.True(g.Root.ContainsKey(g.Path, [ids[2], ids[1]]));
        }

        /// <summary>
        /// The memo: a second lift over unchanged cases and parent does nothing; once a case changes, it lifts again.
        /// </summary>
        [Fact]
        public void TheMemoSkipsAnUnchangedLift()
        {
            var (g, ids) = Setup(3);
            using var set = new CaseSet(g.Root);
            var one = set.AddCase();
            var two = set.AddCase();
            one.StageUnion(ids[0], ids[1]);
            two.StageUnion(ids[0], ids[1]);

            var first = set.Lift(Spec(g), LiftOptions.Default);
            Assert.True(first.Changed);
            var again = set.Lift(Spec(g), LiftOptions.Default);
            Assert.False(again.Changed);
            Assert.Equal(0, again.Rounds);
            Assert.Equal(2, again.LiveCases);

            g.Assert(one, g.Mark, ids[2]);
            g.Assert(two, g.Mark, ids[2]);
            var third = set.Lift(Spec(g), LiftOptions.Default);
            Assert.True(third.Changed);
            Assert.True(g.Root.ContainsKey(g.Mark, [ids[2]]));
        }

        /// <summary>
        /// A function with an id output (here <c>add</c> lifted as a function): written when one parent id stands for
        /// every case's output; skipped when the outputs are local terms with no common parent id.
        /// </summary>
        [Fact]
        public void IdOutputFunctionsNeedACommonParentId()
        {
            var (g, ids) = Setup(4);
            var spec = new LiftSpec(new LiftTable(g.Add, LiftTableKind.Function));
            using var set = new CaseSet(g.Root);
            for (var i = 0; i < 2; i++)
            {
                var @case = set.AddCase();
                var common = @case.AddTerm(g.Add, [ids[0], ids[1]]);
                @case.StageUnion(common, ids[2]);
                @case.AddTerm(g.Add, [ids[1], ids[0]]);
            }

            var report = set.Lift(spec, LiftOptions.Default);
            Assert.Equal(1, report.Rows);
            Assert.Equal(0, report.Terms);
            Assert.Equal(g.Root.Find(ids[2]), g.Root.Find(g.Root.LookupId(g.Add, [ids[0], ids[1]])!.Value));
            Assert.Null(g.Root.LookupId(g.Add, [ids[1], ids[0]]));

            // Differing parent outputs have no common member: nothing is written.
            var (h, hid) = Setup(4);
            using var split = new CaseSet(h.Root);
            for (var i = 0; i < 2; i++)
            {
                var @case = split.AddCase();
                var term = @case.AddTerm(h.Add, [hid[0], hid[1]]);
                @case.StageUnion(term, hid[2 + i]);
            }

            var none = split.Lift(new LiftSpec(new LiftTable(h.Add, LiftTableKind.Function)), LiftOptions.Default);
            Assert.Equal(0, none.Rows);
            Assert.Null(h.Root.LookupId(h.Add, [hid[0], hid[1]]));
        }

        /// <summary>
        /// Lifted rows are written not subsumed, whatever the cases' rows say; the cases keep theirs.
        /// </summary>
        [Fact]
        public void LiftedRowsAreNotSubsumed()
        {
            var (g, ids) = Setup(2);
            var tag = g.EGraph.AddTable(new FunctionConfig
            {
                Schema = [ColumnTy.Id, g.UnitType],
                Default = DefaultVal.Const(g.Unit),
                Merge = MergeFn.AssertEq,
                Name = "tag",
                CanSubsume = true,
            });

            var rb = g.EGraph.NewRule("subsume-tag", true);
            var x = rb.NewVar(ColumnTy.Id);
            rb.QueryTable(tag, [x, rb.NewVar(g.UnitType)], false);
            rb.Subsume(tag, [x]);
            var subsume = rb.Build();

            using var set = new CaseSet(g.Root);
            for (var i = 0; i < 2; i++)
            {
                var @case = set.AddCase();
                @case.StageRow(tag, [ids[0], g.Unit]);
                @case.RunRules(subsume);
                var subsumed = false;
                @case.ForEach(tag, row => subsumed |= row.Subsumed);
                Assert.True(subsumed);
            }

            var report = set.Lift(new LiftSpec(new LiftTable(tag, LiftTableKind.Relation)), LiftOptions.Default);
            Assert.Equal(1, report.Rows);
            var flags = new System.Collections.Generic.List<bool>();
            g.Root.ForEach(tag, row => flags.Add(row.Subsumed));
            Assert.Equal([false], flags);
            foreach (var @case in set.Cases)
            {
                var stillSubsumed = false;
                @case.ForEach(tag, row => stillSubsumed |= row.Subsumed);
                Assert.True(stillSubsumed);
            }
        }

        /// <summary>
        /// A lift into a child of a frozen root writes into that child, and its parent sees nothing of it.
        /// </summary>
        [Fact]
        public void ALiftIntoAChildScopeStaysThere()
        {
            var (g, ids) = Setup(3);
            g.Root.Freeze();
            using var scope = g.Root.CreateChild();
            using (var set = new CaseSet(scope))
            {
                for (var i = 0; i < 2; i++)
                {
                    var @case = set.AddCase();
                    @case.StageUnion(ids[0], ids[1]);
                    g.Assert(@case, g.Mark, ids[2]);
                }

                set.Lift(Spec(g), LiftOptions.Default);
                Assert.Equal(scope.Find(ids[0]), scope.Find(ids[1]));
                Assert.True(scope.ContainsKey(g.Mark, [ids[2]]));
                CheckAll(scope, set);
            }

            Assert.NotEqual(g.Root.Find(ids[0]), g.Root.Find(ids[1]));
            Assert.False(g.Root.ContainsKey(g.Mark, [ids[2]]));
        }

        [Fact]
        public void BadArgumentsAreRefused()
        {
            var (g, ids) = Setup(2);
            Assert.Throws<ArgumentException>(() => new LiftSpec(new LiftTable(g.Mark, LiftTableKind.Relation), new LiftTable(g.Mark, LiftTableKind.Function)));
            Assert.Throws<ArgumentOutOfRangeException>(() => new LiftOptions { RoundLimit = 0 });
            Assert.Throws<ArgumentOutOfRangeException>(() => new LiftOptions { CandidateLimit = 0 });

            using var set = new CaseSet(g.Root);
            Assert.Throws<ArgumentException>(() => set.AddCase(ContextOptions.Default));
            var @case = set.AddCase();
            Assert.Throws<ArgumentException>(() => set.Lift(new LiftSpec(new LiftTable(g.Low, LiftTableKind.Constructor)), LiftOptions.Default));
            Assert.Throws<ArgumentException>(() => set.Lift(new LiftSpec(new LiftTable(g.Num, LiftTableKind.Function, (a, _) => a)), LiftOptions.Default));

            // Cases of another parent, and a frozen parent, are refused.
            LiftSignature? memo = null;
            using var grandchild = @case.CreateChild(ContextOptions.Case);
            Assert.Throws<ArgumentException>(() => Lifter.Lift(g.Root, [grandchild], Spec(g), LiftOptions.Default, ref memo));
            grandchild.Dispose();
            var (h, _) = Setup(1);
            h.Root.Freeze();
            Assert.Throws<InvalidOperationException>(() => new CaseSet(h.Root));

            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            Assert.Throws<OperationCanceledException>(() => set.Lift(Spec(g), LiftOptions.Default, cancelled.Token));
            _ = ids;
        }

        /// <summary>
        /// Disposal: in reverse creation order, every case of the set and the set itself; a second call does nothing.
        /// </summary>
        [Fact]
        public void DisposingTheSetDisposesItsCases()
        {
            var (g, _) = Setup(1);
            var set = new CaseSet(g.Root);
            var cases = Enumerable.Range(0, 3).Select(_ => set.AddCase()).ToArray();
            set.Dispose();
            Assert.All(cases, c => Assert.True(c.IsDisposed));
            Assert.True(set.IsDisposed);
            set.Dispose();
            Assert.Throws<ObjectDisposedException>(() => set.AddCase());
        }

        /// <summary>
        /// A case join applies to <see cref="LiftTableKind.Function"/> only, and a relation with an id output is
        /// refused (its seed output could be local to the seed, and no local id may reach the parent): both before
        /// anything is written.
        /// </summary>
        [Fact]
        public void MisplacedJoinsAndIdRelationsAreRefused()
        {
            var (g, ids) = Setup(1);
            using var set = new CaseSet(g.Root);
            set.AddCase().AddTerm(g.Num, [g.Int(7)]);
            var version = g.Root.Version;

            Assert.Throws<ArgumentException>(() => set.Lift(new LiftSpec(new LiftTable(g.Mark, LiftTableKind.Relation, (a, _) => a)), LiftOptions.Default));
            Assert.Throws<ArgumentException>(() => set.Lift(new LiftSpec(new LiftTable(g.Num, LiftTableKind.Relation)), LiftOptions.Default));
            Assert.Equal(version, g.Root.Version);
            _ = ids;
        }

        /// <summary>
        /// A relation-kind table with a base output lifts a key only where every live case holds the same output.
        /// </summary>
        [Fact]
        public void ARelationKindLiftsABaseOutputTheCasesAgreeOn()
        {
            var (g, ids) = Setup(2);
            using var set = new CaseSet(g.Root);
            var one = set.AddCase();
            var two = set.AddCase();
            one.StageRow(g.Low, [ids[0], g.Int(4)]);
            two.StageRow(g.Low, [ids[0], g.Int(4)]);
            one.StageRow(g.Low, [ids[1], g.Int(1)]);
            two.StageRow(g.Low, [ids[1], g.Int(2)]);

            set.Lift(new LiftSpec(new LiftTable(g.Low, LiftTableKind.Relation)), LiftOptions.Default);
            Assert.Equal<Value?>(g.Int(4), g.Root.LookupId(g.Low, [ids[0]]));
            Assert.Null(g.Root.LookupId(g.Low, [ids[1]]));
        }

        /// <summary>
        /// A <see cref="LiftTableKind.Relation"/> table writes the output every live case holds (bridge relations have
        /// a unit output). A function with another base output listed as a relation is not lifted with one case's
        /// value: here <c>low(a)</c> is 1 in one case and 5 in the other, so nothing is written, where writing 1 into
        /// the root would make the second case see <c>low(a) = 1</c>. A refusal (the lift's
        /// <see cref="ArgumentException"/> for a function that does not fit its kind) passes too.
        /// </summary>
        [Fact]
        public void ARelationKindWithABaseOutputIsNotLiftedFromOneCase()
        {
            var (g, ids) = Setup(1);
            var a = ids[0];
            using var set = new CaseSet(g.Root);
            var one = set.AddCase();
            one.StageRow(g.Low, [a, g.Int(1)]);
            var two = set.AddCase();
            two.StageRow(g.Low, [a, g.Int(5)]);
            var spec = new LiftSpec(new LiftTable(g.Low, LiftTableKind.Relation));

            var refused = Record.Exception(() => set.Lift(spec, LiftOptions.Default));
            if (refused is not null)
            {
                Assert.IsType<ArgumentException>(refused);
                return;
            }

            two.EnsureClean();
            Assert.Null(g.Root.LookupId(g.Low, [a]));
            Assert.Equal(g.Int(5), two.LookupId(g.Low, [a]));
        }

        static uint Max(ContextGraph g, uint a, uint b)
        {
            var values = g.EGraph.BaseValues;
            return values.Get<Int64Codec, long>(Math.Max(values.Unwrap<Int64Codec, long>(new Value(a)), values.Unwrap<Int64Codec, long>(new Value(b)))).Rep;
        }

        /// <summary>
        /// <see cref="LiftTableKind.Relation"/> listed for a function with a base output never writes a value one live
        /// case does not hold: <c>low(a)</c> is 3 in one case and 5 in another, and a lifted fact holds in every live
        /// case. Either the kind is refused for such a function, or the value written holds in every live case.
        /// </summary>
        [Fact]
        public void ARelationKindNeverLiftsAValueOneLiveCaseDoesNotHold()
        {
            var (g, ids) = Setup(1);
            using var set = new CaseSet(g.Root);
            set.AddCase().StageRow(g.Low, [ids[0], g.Int(3)]);
            set.AddCase().StageRow(g.Low, [ids[0], g.Int(5)]);

            try
            {
                set.Lift(new LiftSpec(new LiftTable(g.Low, LiftTableKind.Relation)), LiftOptions.Default);
            }
            catch (ArgumentException)
            {
                return;
            }

            var lifted = g.Root.LookupId(g.Low, [ids[0]]);
            Assert.True(lifted is null || lifted == g.Int(5), $"low(a) = {(lifted is { } v ? g.EGraph.BaseValues.Unwrap<Int64Codec, long>(v) : 0)} was lifted, but one live case holds low(a) = 5");
        }

        /// <summary>
        /// The bridge's lift reads (<c>EGraphContext.Lift.cs</c>): <c>IsLocalId</c>, <c>ScanMerges</c>, <c>ScanOwn</c>,
        /// <c>ScanAncestorsMentioning</c> and the member map are refused at the root.
        /// </summary>
        [Fact]
        public void TheLiftReadsAreRefusedAtTheRoot()
        {
            var (g, ids) = Setup(1);
            g.Assert(g.Root, g.Mark, ids[0]);
            g.Root.FlushUpdates();
            Assert.Throws<InvalidOperationException>(() => g.Root.IsLocalId(ids[0]));
            Assert.Throws<InvalidOperationException>(() =>
            {
                var sink = new RowCountSink();
                g.Root.ScanOwn(g.Mark, ref sink);
            });
            Assert.Throws<InvalidOperationException>(() =>
            {
                var sink = new RowCountSink();
                g.Root.ScanAncestorsMentioning(g.Mark, ids[0].Rep, ref sink);
            });
            Assert.Throws<InvalidOperationException>(() =>
            {
                var visitor = new MergeCollector();
                g.Root.ScanMerges(ref visitor);
            });
            Assert.Throws<InvalidOperationException>(() => g.Root.GetMemberMap());
        }

        /// <summary>
        /// The lift checks its token between rounds, not only before the first. A case join cancels the token during
        /// round 1 of a lift that needs two rounds (terms bottom-up); round 2 does not run.
        /// </summary>
        [Fact]
        public void TheTokenIsCheckedBetweenRounds()
        {
            var (g, ids) = Setup(1);
            using var cancellation = new CancellationTokenSource();
            using var set = new CaseSet(g.Root);
            for (var i = 0; i < 2; i++)
            {
                var @case = set.AddCase();
                var one = @case.AddTerm(g.Num, [g.Int(1)]);
                @case.AddTerm(g.Add, [one, one]);
                @case.StageRow(g.Low, [ids[0], g.Int(i)]);
            }

            var spec = new LiftSpec(
                new LiftTable(g.Num, LiftTableKind.Constructor),
                new LiftTable(g.Add, LiftTableKind.Constructor),
                new LiftTable(g.Low, LiftTableKind.Function, (a, b) =>
                {
                    cancellation.Cancel();
                    return Max(g, a, b);
                }));

            Assert.Throws<OperationCanceledException>(() => set.Lift(spec, LiftOptions.Default, cancellation.Token));
            Assert.NotNull(g.Root.LookupId(g.Num, [g.Int(1)]));
            Assert.Equal(g.Int(1), g.Root.LookupId(g.Low, [ids[0]]));
            Assert.Equal(0, g.Root.TableSize(g.Add));
            Assert.False(g.Root.IsFaulted);
            foreach (var @case in set.Cases)
            {
                @case.EnsureClean();
                @case.CheckInvariants();
            }
        }

    }

}

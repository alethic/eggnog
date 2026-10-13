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
    /// Lifts over cases that are all vacuous contradict the parent: whether the set disposed the vacuous cases before
    /// the lift, all at once or in two steps, or not; and a memoized repeat still reports it. The parent's
    /// contradiction row names no case-local id, and a case disposed outside the set is not taken for a vacuous one.
    /// </summary>
    public class LiftVacuousCaseTests
    {

        /// <summary>
        /// A contradiction function whose output is a base value with no constant default
        /// (<c>DesignateContradiction</c> accepts any nullary function): every case writes it and is disposed as
        /// vacuous by the set (<see cref="CaseSet.DisposeVacuous"/>, or <see cref="CaseSet.RunEach"/>). A lift over
        /// that set contradicts the parent, as a lift over the same cases before they were disposed does.
        /// </summary>
        [Theory]
        [InlineData(0)]
        [InlineData(1)]
        [InlineData(2)]
        public void AnAllVacuousSetDisposedBeforeTheLiftContradictsTheParent(int disposeFirst)
        {
            var g = new ContextGraph();
            var bad = g.EGraph.AddTable(new FunctionConfig
            {
                Schema = [g.I64],
                Default = DefaultVal.Fail,
                Merge = MergeFn.Primitive(g.Min, MergeFn.Old, MergeFn.New).AsLattice(),
                Name = "bad",
            });
            g.EGraph.DesignateContradiction(bad);
            _ = g.Root.FreshId();

            using var set = new CaseSet(g.Root);
            for (var i = 0; i < 2; i++)
            {
                var @case = set.AddCase();
                @case.StageRow(bad, [Int(g.EGraph, 1)]);
                @case.EnsureClean();
                Assert.True(@case.IsStopped);
            }

            if (disposeFirst == 1)
                Assert.Equal(2, set.DisposeVacuous());
            else if (disposeFirst == 2)
                Assert.False(set.RunEach(new RecordingSchedule((_, _, _) => true), 0, allowSplits: false));

            Assert.Equal(disposeFirst == 0 ? 2 : 0, set.Cases.Count);

            LiftReport? report = null;
            var thrown = Record.Exception(() => report = set.Lift(LiftSpec.Empty, LiftOptions.Default));
            Assert.True(thrown is null, $"{thrown?.GetType().Name}: {thrown?.Message}");
            Assert.True(report!.ParentContradicted);
            Assert.True(g.Root.IsContradictory);
        }

        /// <summary>
        /// A graph whose contradiction function is the context graph's <c>bottom</c>, with <paramref name="count"/>
        /// root ids.
        /// </summary>
        static (ContextGraph Graph, Value[] Ids) Setup(int count)
        {
            var g = new ContextGraph();
            g.EGraph.DesignateContradiction(g.Bottom);
            var ids = Enumerable.Range(0, count).Select(_ => g.Root.FreshId()).ToArray();
            return (g, ids);
        }

        /// <summary>
        /// When every case is vacuous, the parent's contradiction row names no id local to a case. The contradiction
        /// function here is a nullary constructor (<c>DesignateContradiction</c> accepts any nullary function, and the
        /// lift handles id outputs); each case's own contradiction term is a fresh id of that case.
        /// </summary>
        [Fact]
        public void TheAllVacuousContradictionNamesNoLocalId()
        {
            var g = new ContextGraph();
            var bot = Constructor(g.EGraph, "bot", false);
            g.EGraph.DesignateContradiction(bot);
            var a = g.Root.FreshId();

            using var set = new CaseSet(g.Root);
            var cases = new EGraphContext[2];
            for (var i = 0; i < 2; i++)
            {
                var @case = cases[i] = set.AddCase();
                @case.StageTerm(bot, ReadOnlySpan<Value>.Empty);
                @case.EnsureClean();
                Assert.True(@case.IsStopped);
            }

            var report = set.Lift(new LiftSpec(new LiftTable(g.Mark, LiftTableKind.Relation)), LiftOptions.Default);
            Assert.True(report.ParentContradicted);
            Assert.True(g.Root.IsContradictory);
            var output = g.Root.LookupId(bot, ReadOnlySpan<Value>.Empty);
            Assert.NotNull(output);
            foreach (var @case in cases)
                Assert.False(@case.IsLocalId(output!.Value), $"the root's contradiction row names {output!.Value.Rep}, an id local to a case");

            _ = a;
        }

        static LiftSpec Spec(ContextGraph g) => new(
            new LiftTable(g.Edge, LiftTableKind.Relation),
            new LiftTable(g.Path, LiftTableKind.Relation),
            new LiftTable(g.Mark, LiftTableKind.Relation),
            new LiftTable(g.Num, LiftTableKind.Constructor),
            new LiftTable(g.Add, LiftTableKind.Constructor),
            new LiftTable(g.Low, LiftTableKind.Function, (a, b) => Max(g, a, b)));

        static uint Max(ContextGraph g, uint a, uint b)
        {
            var values = g.EGraph.BaseValues;
            return values.Get<Int64Codec, long>(Math.Max(values.Unwrap<Int64Codec, long>(new Value(a)), values.Unwrap<Int64Codec, long>(new Value(b)))).Rep;
        }

        /// <summary>
        /// A case disposed outside the set (not found vacuous by it) leaves the cases' coverage: <c>b</c> is split over
        /// <c>{t, f}</c> and the case <c>b = f</c> is disposed by its owner, not because it is contradictory. The set
        /// does not lift <c>b = t</c> from the one remaining case as if the disposed one had been vacuous (only vacuous
        /// cases are dropped); refusing the lift is the sound answer.
        /// </summary>
        [Fact]
        public void ACaseDisposedOutsideTheSetIsNotTreatedAsVacuous()
        {
            var (g, ids) = Setup(3);
            var (t, f, b) = (ids[0], ids[1], ids[2]);
            using var set = new CaseSet(g.Root);
            set.AddCase().StageUnion(b, t);
            var other = set.AddCase();
            other.StageUnion(b, f);
            other.Dispose();

            try
            {
                set.Lift(Spec(g), LiftOptions.Default);
            }
            catch (InvalidOperationException)
            {
                return;
            }

            Assert.True(g.Root.Find(b) != g.Root.Find(t), "b = t was lifted from one case of two; the other was disposed, not found vacuous");
        }

        /// <summary>
        /// A lift whose cases are all vacuous reports <see cref="LiftReport.ParentContradicted"/>. A second lift over
        /// the same unchanged set hits the memo; its report still says what holds (no live case, the parent
        /// contradicted), not <c>ParentContradicted = false</c> with zero live cases.
        /// </summary>
        [Fact]
        public void AMemoizedAllVacuousLiftStillReportsTheContradiction()
        {
            var (g, _) = Setup(1);
            using var set = new CaseSet(g.Root);
            set.AddCase().StageRow(g.Bottom, [g.Unit]);
            set.AddCase().StageRow(g.Bottom, [g.Unit]);

            var first = set.Lift(Spec(g), LiftOptions.Default);
            Assert.True(first.ParentContradicted);
            var again = set.Lift(Spec(g), LiftOptions.Default);
            Assert.Equal(0, again.LiveCases);
            Assert.True(again.ParentContradicted, "every case is vacuous and the parent is contradictory, but the memoized lift reports ParentContradicted = false");
        }

        /// <summary>
        /// A base-output contradiction function with no constant default, mixing the two paths: the set disposes the
        /// first vacuous case (<see cref="CaseSet.DisposeVacuous"/>), keeping its output, while the second case is
        /// still live; the second case then goes vacuous and is either lifted undisposed (<c>disposeSecond</c> false)
        /// or disposed by the set first. Either way the lift contradicts the parent.
        /// </summary>
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void AnAllVacuousSetDisposedInTwoStepsContradictsTheParent(bool disposeSecond)
        {
            var g = new ContextGraph();
            var bad = g.EGraph.AddTable(new FunctionConfig
            {
                Schema = [g.I64],
                Default = DefaultVal.Fail,
                Merge = MergeFn.Primitive(g.Min, MergeFn.Old, MergeFn.New).AsLattice(),
                Name = "bad",
            });
            g.EGraph.DesignateContradiction(bad);
            _ = g.Root.FreshId();

            using var set = new CaseSet(g.Root);
            var first = set.AddCase();
            var second = set.AddCase();
            first.StageRow(bad, [Int(g.EGraph, 3)]);
            first.EnsureClean();
            Assert.Equal(1, set.DisposeVacuous());
            Assert.Single(set.Cases);

            second.StageRow(bad, [Int(g.EGraph, 5)]);
            second.EnsureClean();
            if (disposeSecond)
                Assert.Equal(1, set.DisposeVacuous());

            var report = set.Lift(LiftSpec.Empty, LiftOptions.Default);
            Assert.True(report.ParentContradicted);
            Assert.True(g.Root.IsContradictory);
            g.Root.CheckInvariants();
        }

    }

}

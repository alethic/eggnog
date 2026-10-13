using System;
using System.Linq;

using Xunit;

using static Eggnog.Relations.Tests.BridgeTestUtil;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// Review of colors M10.4 under the soundness lens (colored-contexts spec §11.2, I10, I33; rule-layer E8): the memo,
    /// the kinds' output contracts, the all-vacuous contradiction row and the lift of id-output functions.
    /// </summary>
    public class LiftSoundnessReviewTests
    {

        /// <summary>
        /// A graph whose contradiction function is the context graph's <c>bottom</c>, with <paramref name="count"/> root ids.
        /// </summary>
        static (ContextGraph Graph, Value[] Ids) Setup(int count)
        {
            var g = new ContextGraph();
            g.EGraph.DesignateContradiction(g.Bottom);
            var ids = Enumerable.Range(0, count).Select(_ => g.Root.FreshId()).ToArray();
            return (g, ids);
        }

        /// <summary>
        /// The memo of a case set must not skip a lift whose spec differs from the one memoized: <c>mark(a)</c> holds in
        /// every case, and the second lift lists <c>mark</c> for the first time (rule-layer §6.9.1 rebuilds the spec when a
        /// table is defined, and host-driven lifts pass it on every call).
        /// </summary>
        [Fact]
        public void TheMemoDoesNotSkipALiftWithAnotherSpec()
        {
            var (g, ids) = Setup(2);
            var (a, b) = (ids[0], ids[1]);
            using var set = new CaseSet(g.Root);
            for (var i = 0; i < 2; i++)
            {
                var @case = set.AddCase();
                g.Assert(@case, g.Mark, a);
                g.Assert(@case, g.Edge, a, b);
            }

            set.Lift(new LiftSpec(new LiftTable(g.Edge, LiftTableKind.Relation)), LiftOptions.Default);
            Assert.True(g.Root.ContainsKey(g.Edge, [a, b]));

            set.Lift(new LiftSpec(new LiftTable(g.Mark, LiftTableKind.Relation)), LiftOptions.Default);
            Assert.True(g.Root.ContainsKey(g.Mark, [a]), "mark(a) holds in every case, but the memo skipped the lift that lists mark");
        }

        /// <summary>
        /// The memo of a case set must not skip a lift whose candidate limit is wider than the one memoized: the first lift
        /// skips <c>mark(a)</c> (two candidates over a limit of one) and reports it in <see cref="LiftReport.Skipped"/>;
        /// the second, with the default limit, must lift it.
        /// </summary>
        [Fact]
        public void TheMemoDoesNotSkipALiftWithAWiderCandidateLimit()
        {
            var (g, ids) = Setup(3);
            var (a, b, c) = (ids[0], ids[1], ids[2]);
            using var set = new CaseSet(g.Root);
            var one = set.AddCase();
            one.StageUnion(a, b);
            g.Assert(one, g.Mark, a);
            var two = set.AddCase();
            two.StageUnion(a, c);
            g.Assert(two, g.Mark, a);
            var spec = new LiftSpec(new LiftTable(g.Mark, LiftTableKind.Relation));

            var narrow = set.Lift(spec, new LiftOptions { CandidateLimit = 1 });
            Assert.True(narrow.Skipped > 0);
            Assert.False(g.Root.ContainsKey(g.Mark, [a]));

            var wide = set.Lift(spec, LiftOptions.Default);
            Assert.True(g.Root.ContainsKey(g.Mark, [a]), $"mark(a) holds in every case, but the memo skipped the wider lift (Changed = {wide.Changed})");
        }

        /// <summary>
        /// A <see cref="LiftTableKind.Relation"/> table writes the seed's output (bridge relations have a unit output). A
        /// function with another output listed as a relation must be refused (the lift's documented ArgumentException for
        /// a function that does not fit its kind), not lifted with one case's value: here <c>low(a)</c> is 1 in one case
        /// and 5 in the other, and writing 1 into the root makes the second case see <c>low(a) = 1</c>.
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

        /// <summary>
        /// I10: no id local to a case reaches its parent. A function with an id output listed as a relation writes the
        /// seed's canonical output, which here is a fresh id of the seed case.
        /// </summary>
        [Fact]
        public void ARelationKindWithAnIdOutputLeaksNoLocalId()
        {
            var (g, ids) = Setup(1);
            var a = ids[0];
            var pick = g.EGraph.AddTable(new FunctionConfig
            {
                Schema = [ColumnTy.Id, ColumnTy.Id],
                Default = DefaultVal.Fail,
                Merge = MergeFn.UnionId,
                Name = "pick",
            });

            using var set = new CaseSet(g.Root);
            var cases = new EGraphContext[2];
            for (var i = 0; i < 2; i++)
            {
                var @case = cases[i] = set.AddCase();
                @case.StageRow(pick, [a, @case.FreshId()]);
            }

            var spec = new LiftSpec(new LiftTable(pick, LiftTableKind.Relation));
            var refused = Record.Exception(() => set.Lift(spec, LiftOptions.Default));
            if (refused is not null)
            {
                Assert.True(refused is ArgumentException, $"expected a refusal of the kind before anything is written, got {refused.GetType().Name}: {refused.Message}; root faulted: {g.Root.IsFaulted}");
                return;
            }

            if (g.Root.LookupId(pick, [a]) is { } output)
                foreach (var @case in cases)
                    Assert.False(@case.IsLocalId(output), $"the root's pick(a) = {output.Rep} is an id local to a case (I10)");
        }

        /// <summary>
        /// I10 and L4: when every case is vacuous, the parent's contradiction row must not name an id local to a case.
        /// The contradiction function here is a nullary constructor (DesignateContradiction accepts any nullary
        /// function, and the lift handles id outputs); each case's own contradiction term is a fresh id of that case.
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
                Assert.False(@case.IsLocalId(output!.Value), $"the root's contradiction row names {output!.Value.Rep}, an id local to a case (I10)");

            _ = a;
        }

        /// <summary>
        /// I33 soundness and the root's semantics: a function with an id output whose merge asserts equality
        /// (<see cref="MergeFn.AssertEq"/>, a lattice merge) holds <c>pick(a) = e</c> in the root, and every case merges
        /// <c>e</c> with <c>x</c>. The lift's meet writes the union; the id-output function path must not also write
        /// <c>pick(a) = x</c> against the stored <c>pick(a) = e</c>, which the merge refuses as a conflict.
        /// </summary>
        [Fact]
        public void AnIdOutputFunctionLiftDoesNotConflictWithItsStoredRow()
        {
            var (g, ids) = Setup(3);
            var (a, x, e) = (ids[0], ids[1], ids[2]);
            var pick = g.EGraph.AddTable(new FunctionConfig
            {
                Schema = [ColumnTy.Id, ColumnTy.Id],
                Default = DefaultVal.Fail,
                Merge = MergeFn.AssertEq,
                Name = "pick",
            });

            g.Root.StageRow(pick, [a, e]);
            g.Root.FlushUpdates();

            using var set = new CaseSet(g.Root);
            for (var i = 0; i < 2; i++)
                set.AddCase().StageUnion(e, x);

            var report = set.Lift(new LiftSpec(new LiftTable(pick, LiftTableKind.Function)), LiftOptions.Default);
            Assert.Equal(1, report.Unions);
            Assert.Equal(g.Root.Find(x), g.Root.Find(e));
            Assert.Equal(g.Root.Find(e), g.Root.LookupId(pick, [a]));
        }

        /// <summary>
        /// The same as <see cref="AnIdOutputFunctionLiftDoesNotConflictWithItsStoredRow"/>, lifted into a writable child of
        /// a frozen root.
        /// </summary>
        [Fact]
        public void AnIdOutputFunctionLiftIntoAChildDoesNotConflictWithItsStoredRow()
        {
            var (g, ids) = Setup(3);
            var (a, x, e) = (ids[0], ids[1], ids[2]);
            var pick = g.EGraph.AddTable(new FunctionConfig
            {
                Schema = [ColumnTy.Id, ColumnTy.Id],
                Default = DefaultVal.Fail,
                Merge = MergeFn.AssertEq,
                Name = "pick",
            });

            g.Root.StageRow(pick, [a, e]);
            g.Root.FlushUpdates();
            g.Root.Freeze();
            using var scope = g.Root.CreateChild();

            using var set = new CaseSet(scope);
            for (var i = 0; i < 2; i++)
                set.AddCase().StageUnion(e, x);

            LiftReport? report = null;
            var thrown = Record.Exception(() => report = set.Lift(new LiftSpec(new LiftTable(pick, LiftTableKind.Function)), LiftOptions.Default));
            Assert.True(thrown is null, $"{thrown?.GetType().Name}: {thrown?.Message}; scope faulted: {scope.IsFaulted}");
            Assert.Equal(1, report!.Unions);
            Assert.False(scope.IsFaulted);
            Assert.Equal(scope.Find(x), scope.Find(e));
            Assert.Equal(scope.Find(e), scope.LookupId(pick, [a]));
        }

        /// <summary>
        /// C7 safeguard (colored-contexts spec §7.2 (c′), §19 item 9): the rows a lift writes into the parent reach the cases
        /// through catch-up without a re-stamped copy, over several rounds (terms lifted bottom-up, then equalities and
        /// facts over them), into the root and into a writable child of a frozen root.
        /// </summary>
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void LiftedRowsReachTheCasesWithoutRestampedCopies(bool childParent)
        {
            var (g, ids) = Setup(4);
            g.Assert(g.Root, g.Edge, ids[0], ids[2]);
            g.Assert(g.Root, g.Edge, ids[1], ids[2]);
            g.Root.FlushUpdates();
            var copy = g.EdgeToPath();
            var transitive = g.Transitive();
            using var scope = childParent ? Child(g) : null;
            var parent = scope ?? g.Root;

            using var set = new CaseSet(parent);
            var cases = new EGraphContext[2];
            for (var i = 0; i < 2; i++)
            {
                var @case = cases[i] = set.AddCase();
                @case.StageUnion(ids[3], ids[i]);
                var one = @case.StageTerm(g.Num, [g.Int(1)]);
                var sum = @case.StageTerm(g.Add, [one, ids[3]]);
                g.Assert(@case, g.Edge, sum, ids[3]);
                @case.EnsureClean();
                @case.Saturate([copy, transitive], 8);
            }

            var spec = new LiftSpec(
                new LiftTable(g.Edge, LiftTableKind.Relation),
                new LiftTable(g.Path, LiftTableKind.Relation),
                new LiftTable(g.Num, LiftTableKind.Constructor),
                new LiftTable(g.Add, LiftTableKind.Constructor));
            var report = set.Lift(spec, LiftOptions.Default);
            Assert.True(report.Rounds >= 2);
            foreach (var @case in cases)
            {
                @case.EnsureClean();
                @case.Saturate([copy, transitive], 8);
                Assert.Equal(0, @case.Context.Tables?.Statistics.RestampedCopies ?? 0);
            }
        }

        static EGraphContext Child(ContextGraph g)
        {
            g.Root.Freeze();
            return g.Root.CreateChild();
        }

        /// <summary>
        /// L4 and the memo: a second lift over the same all-vacuous cases still reports the parent contradicted, as the
        /// first did (the split driver ends its step on <see cref="LiftReport.ParentContradicted"/>).
        /// </summary>
        [Fact]
        public void AMemoizedAllVacuousLiftStillReportsTheContradiction()
        {
            var (g, _) = Setup(1);
            using var set = new CaseSet(g.Root);
            for (var i = 0; i < 2; i++)
                set.AddCase().StageRow(g.Bottom, [g.Unit]);

            var spec = new LiftSpec(new LiftTable(g.Mark, LiftTableKind.Relation));
            var first = set.Lift(spec, LiftOptions.Default);
            Assert.True(first.ParentContradicted);
            var second = set.Lift(spec, LiftOptions.Default);
            Assert.Equal(0, second.LiveCases);
            Assert.True(second.ParentContradicted, "every case is vacuous, but the memoized lift reports the parent not contradicted");
        }

    }

}

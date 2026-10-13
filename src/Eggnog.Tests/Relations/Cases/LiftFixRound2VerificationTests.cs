using Xunit;

using static Eggnog.Relations.Tests.BridgeTestUtil;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// Second verification of the colors M10.4 fixes (colored-contexts spec §11.2, L4): the all-vacuous contradiction once
    /// the set disposed every case.
    /// </summary>
    public class LiftFixRound2VerificationTests
    {

        /// <summary>
        /// L4 with a contradiction function whose output is a base value with no constant default (DesignateContradiction
        /// accepts any nullary function): every case writes it and is disposed as vacuous by the set
        /// (<see cref="CaseSet.DisposeVacuous"/>, or <see cref="CaseSet.RunEach"/>). A lift over that set contradicts the
        /// parent (rule-layer N51), as a lift over the same cases before they were disposed does.
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
        /// The id-output function path writes over a key the parent holds only when every live case moved off the stored
        /// class, which an <see cref="MergeFn.AssertEq"/> merge allows only through a failed merge. A case whose
        /// assert-eq write conflicted with the parent's row must not lead the lift to write that conflict into the parent:
        /// the lift is refused or leaves the parent's row as it was, and the parent is never faulted.
        /// </summary>
        [Fact]
        public void AFailedAssertEqWriteInACaseNeverReachesTheParent()
        {
            var g = new ContextGraph();
            g.EGraph.DesignateContradiction(g.Bottom);
            var (k, e, x) = (g.Root.FreshId(), g.Root.FreshId(), g.Root.FreshId());
            var pick = g.EGraph.AddTable(new FunctionConfig
            {
                Schema = [ColumnTy.Id, ColumnTy.Id],
                Default = DefaultVal.Fail,
                Merge = MergeFn.AssertEq,
                Name = "pick",
            });
            g.Root.StageRow(pick, [k, e]);
            g.Root.FlushUpdates();

            using var set = new CaseSet(g.Root);
            for (var i = 0; i < 2; i++)
            {
                var @case = set.AddCase();
                @case.StageRow(pick, [k, x]);
                _ = Record.Exception(() => @case.EnsureClean());
            }

            _ = Record.Exception(() => set.Lift(new LiftSpec(new LiftTable(pick, LiftTableKind.Function)), LiftOptions.Default));
            Assert.False(g.Root.IsFaulted);
            Assert.Equal(g.Root.Find(e), g.Root.LookupId(pick, [k]));
            Assert.NotEqual(g.Root.Find(e), g.Root.Find(x));
            g.Root.CheckInvariants();
        }

    }

}

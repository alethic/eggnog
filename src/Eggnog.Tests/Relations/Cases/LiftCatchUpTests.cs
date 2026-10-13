using System;
using System.Linq;

using Xunit;

using static Eggnog.Relations.Tests.BridgeTestUtil;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// Lifted rows and the re-stamp assertion: the rows a lift writes into its parent reach the cases through catch-up
    /// without a re-stamped copy, over random lifts and over the rounds of one lift, and only the raw execution-state
    /// API marks a store as written raw, the one exemption from the assertion.
    /// </summary>
    public class LiftCatchUpTests
    {

        static (ContextGraph Graph, Value[] Ids) Setup(int count)
        {
            var g = new ContextGraph();
            g.EGraph.DesignateContradiction(g.Bottom);
            var ids = Enumerable.Range(0, count).Select(_ => g.Root.FreshId()).ToArray();
            return (g, ids);
        }

        static LiftSpec MarkSpec(ContextGraph g) => new(new LiftTable(g.Mark, LiftTableKind.Relation));

#if DEBUG
        /// <summary>
        /// The re-stamp assertion exempts only stores written through the raw execution-state API; host writes, rule
        /// runs, lifts and catch-up never mark a store.
        /// </summary>
        [Fact]
        public void OnlyTheRawApiMarksAStoreAsWrittenRaw()
        {
            var (g, ids) = Setup(2);
            var store = g.EGraph.Store;
            g.Assert(g.Root, g.Edge, ids[0], ids[1]);
            g.Root.FlushUpdates();
            g.Root.Saturate([g.EdgeToPath()], 4);
            using (var set = new CaseSet(g.Root))
            {
                var @case = set.AddCase();
                @case.StageUnion(ids[0], ids[1]);
                g.Assert(@case, g.Mark, ids[0]);
                @case.EnsureClean();
                set.Lift(MarkSpec(g), LiftOptions.Default);
            }

            Assert.False(store.HasRawWrites);
            store.Root.Database.WithExecutionState(_ => 0);
            Assert.True(store.HasRawWrites);
        }
#endif

        /// <summary>
        /// Over random lifts into a writable root and into a writable child of a frozen root, with cases that run rules
        /// before and after the lift (so they catch up with the lifted rows), no catch-up copy is ever re-stamped.
        /// </summary>
        [Theory]
        [InlineData(0)]
        [InlineData(1)]
        [InlineData(2)]
        [InlineData(3)]
        public void LiftWorkloadsNeverRestampCatchUpCopies(int batch)
        {
            for (var seed = batch * 50; seed < (batch + 1) * 50; seed++)
            {
                try
                {
                    RunRestampSeed(seed);
                }
                catch (Exception e) when (e is not SeedFailure)
                {
                    throw new SeedFailure(seed, e);
                }
            }
        }

        static void RunRestampSeed(int seed)
        {
            var random = new Random(seed);
            var g = new ContextGraph();
            g.EGraph.DesignateContradiction(g.Bottom);
            var copy = g.EdgeToPath();
            var transitive = g.Transitive();
            var n = 5 + random.Next(5);
            var ids = Enumerable.Range(0, n).Select(_ => g.Root.FreshId()).ToArray();
            Value Pick() => ids[random.Next(ids.Length)];

            for (var i = random.Next(6); i > 0; i--)
                g.Assert(g.Root, g.Edge, Pick(), Pick());

            g.Root.FlushUpdates();
            if (random.Next(2) == 0)
                g.Root.Saturate([copy, transitive], 8);

            EGraphContext? scope = null;
            if (seed % 2 == 1)
            {
                g.Root.Freeze();
                scope = g.Root.CreateChild();
                for (var i = random.Next(4); i > 0; i--)
                    g.Assert(scope, g.Edge, Pick(), Pick());

                scope.FlushUpdates();
                if (random.Next(2) == 0)
                    scope.Saturate([copy, transitive], 8);
            }

            var parent = scope ?? g.Root;
            try
            {
                using var set = new CaseSet(parent);
                var cases = 1 + random.Next(4);
                for (var c = 0; c < cases; c++)
                {
                    var @case = set.AddCase();
                    for (var i = random.Next(4); i > 0; i--)
                        @case.StageUnion(Pick(), Pick());

                    for (var i = random.Next(4); i > 0; i--)
                        g.Assert(@case, g.Edge, Pick(), Pick());

                    for (var i = random.Next(3); i > 0; i--)
                        g.Assert(@case, g.Mark, Pick());

                    @case.EnsureClean();
                    if (@case.IsStopped == false && random.Next(2) == 0)
                        @case.Saturate([copy, transitive], 8);
                }

                for (var round = 0; round < 2; round++)
                {
                    set.Lift(new LiftSpec(
                        new LiftTable(g.Edge, LiftTableKind.Relation),
                        new LiftTable(g.Path, LiftTableKind.Relation),
                        new LiftTable(g.Mark, LiftTableKind.Relation)), LiftOptions.Default);
                    if (parent.IsStopped)
                        break;

                    // The cases catch up with the lifted rows and run their rules again over them.
                    foreach (var @case in set.Cases)
                    {
                        if (@case.IsDisposed || @case.IsStopped)
                            continue;

                        @case.EnsureClean();
                        @case.Saturate([copy, transitive], 8);
                        Assert.Equal(0, @case.Context.Tables?.Statistics.RestampedCopies ?? 0);
                    }
                }

                if (scope is not null)
                    Assert.Equal(0, scope.Context.Tables?.Statistics.RestampedCopies ?? 0);
            }
            finally
            {
                scope?.Dispose();
            }
        }

        /// <summary>
        /// The rows a lift writes into the parent reach the cases through catch-up without a re-stamped copy, over
        /// several rounds (terms lifted bottom-up, then equalities and facts over them), into the root and into a
        /// writable child of a frozen root.
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

    }

}

using System;
using System.Linq;

using Xunit;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// Review probes for colors M10.4 (root-cost lens, §11.2): a lift that writes nothing leaves the root's clock and
    /// version alone; catch-up copies are never re-stamped over lift workloads (§19 item 9); and the lift memo is keyed on
    /// what the lift read, including its spec and limits.
    /// </summary>
    public class LiftReviewTests
    {

        static (ContextGraph Graph, Value[] Ids) Setup(int count)
        {
            var g = new ContextGraph();
            g.EGraph.DesignateContradiction(g.Bottom);
            var ids = Enumerable.Range(0, count).Select(_ => g.Root.FreshId()).ToArray();
            return (g, ids);
        }

        static LiftSpec EdgeSpec(ContextGraph g) => new(new LiftTable(g.Edge, LiftTableKind.Relation));

        static LiftSpec EdgeAndMarkSpec(ContextGraph g) => new(
            new LiftTable(g.Edge, LiftTableKind.Relation),
            new LiftTable(g.Mark, LiftTableKind.Relation));

        /// <summary>
        /// The root departs from egglog only in operations egglog lacks (§4.1): a lift that finds nothing to write leaves
        /// the root's clock, version and rows as they were, and so does its memoized repeat.
        /// </summary>
        [Fact]
        public void ANoOpLiftLeavesTheRootClockAndVersionAlone()
        {
            var (g, ids) = Setup(3);
            g.Assert(g.Root, g.Edge, ids[0], ids[1]);
            g.Root.FlushUpdates();

            using var set = new CaseSet(g.Root);
            var one = set.AddCase();
            var two = set.AddCase();
            g.Assert(one, g.Mark, ids[2]);
            one.EnsureClean();
            two.EnsureClean();

            var clock = g.EGraph.Store.ReadClock();
            var version = g.Root.Version;
            var report = set.Lift(EdgeAndMarkSpec(g), LiftOptions.Default);
            Assert.False(report.Changed);
            Assert.Equal(0, report.Rounds);
            Assert.Equal(clock, g.EGraph.Store.ReadClock());
            Assert.Equal(version, g.Root.Version);

            var again = set.Lift(EdgeAndMarkSpec(g), LiftOptions.Default);
            Assert.False(again.Changed);
            Assert.Equal(clock, g.EGraph.Store.ReadClock());
            Assert.Equal(version, g.Root.Version);
        }

        /// <summary>
        /// §19 item 9 (deferred from M10.3 to M10.4, "once its workloads (the lift) have run under the same assertion"):
        /// over random lifts into a writable root and into a writable child of a frozen root, with cases that run rules
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
        /// The memo is keyed on the versions only (§11.2), not on the limits: a lift whose candidate limit skipped a source
        /// row settles and memoizes; a second lift over the unchanged cases with a limit wide enough to take the row is
        /// skipped by the memo, so a fact that holds in every live case is never lifted, and the second report says nothing
        /// was skipped. Rule-layer §6.9.4 passes the host's <c>LiftOptions</c> on every <c>CaseSet.Lift</c>.
        /// </summary>
        [Fact]
        public void ARaisedCandidateLimitIsNotSkippedByTheMemo()
        {
            var (g, ids) = Setup(6);
            var (a, b, c, d, e, f) = (ids[0], ids[1], ids[2], ids[3], ids[4], ids[5]);
            using var set = new CaseSet(g.Root);

            // The seed (first, one group) merges a, b and c and holds edge(a, d), which stands for three parent keys.
            var one = set.AddCase();
            one.StageUnion(a, b);
            one.StageUnion(b, c);
            g.Assert(one, g.Edge, c, d);

            // The other case holds edge(a, d) without those equalities, and one group of its own.
            var two = set.AddCase();
            g.Assert(two, g.Edge, a, d);
            two.StageUnion(e, f);
            one.EnsureClean();
            two.EnsureClean();

            var narrow = set.Lift(EdgeSpec(g), new LiftOptions { CandidateLimit = 2 });
            Assert.Equal(3, narrow.Skipped);
            Assert.False(g.Root.ContainsKey(g.Edge, [a, d]));

            // edge(a, d) holds in every live case (I33 completeness), and the wider limit admits it.
            var wide = set.Lift(EdgeSpec(g), new LiftOptions { CandidateLimit = 64 });
            Assert.True(g.Root.ContainsKey(g.Edge, [a, d]), $"edge(a, d) holds in every live case but was not lifted (Changed = {wide.Changed}, Rounds = {wide.Rounds}, Skipped = {wide.Skipped})");
        }

        /// <summary>
        /// The memo is not keyed on the spec either (§11.2): after a lift of <c>edge</c> settles, a lift of
        /// <c>edge</c> and <c>mark</c> over the unchanged cases returns at once, so a <c>mark</c> fact that holds in every
        /// live case is never lifted. <c>CaseSet.Lift</c> takes the spec on every call (rule-layer E8).
        /// </summary>
        [Fact]
        public void AWiderSpecIsNotSkippedByTheMemo()
        {
            var (g, ids) = Setup(3);
            using var set = new CaseSet(g.Root);
            var one = set.AddCase();
            var two = set.AddCase();
            g.Assert(one, g.Edge, ids[0], ids[1]);
            g.Assert(two, g.Edge, ids[0], ids[1]);
            g.Assert(one, g.Mark, ids[2]);
            g.Assert(two, g.Mark, ids[2]);

            var first = set.Lift(EdgeSpec(g), LiftOptions.Default);
            Assert.True(first.Changed);
            Assert.True(g.Root.ContainsKey(g.Edge, [ids[0], ids[1]]));

            var wider = set.Lift(EdgeAndMarkSpec(g), LiftOptions.Default);
            Assert.True(g.Root.ContainsKey(g.Mark, [ids[2]]), $"mark(c) holds in every live case but was not lifted (Changed = {wider.Changed}, Rounds = {wider.Rounds})");
        }

    }

}

using System.Linq;

using Xunit;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// Review probe for the item M10.3 deferred to M10.4 (colors §21.10, §10.9): a child's internal parallel rule set
    /// under <see cref="ConcurrentChildren"/>, with more than <see cref="ParallelHeuristics.DbLevelThreshold"/> rows of the
    /// child's own, so its rule sets go parallel by the heuristics (no override) inside a leased body. Each child must end
    /// equal to a serial replay of its program in a fresh serial child of the same frozen root, and pass its invariants.
    /// </summary>
    public class ConcurrentParallelChildrenReviewTests
    {

        const int Sources = 110;
        const int Targets = 100;
        const int Children = 4;

        /// <summary>
        /// Four children of a frozen root, each with four threads (two run at a time under a budget of eight), each asserts
        /// 11,000 edges of its own and saturates <c>edge → path</c> and the transitive rule.
        /// </summary>
        [Fact]
        public void ParallelRuleSetsInConcurrentChildrenEqualSerialReplays()
        {
            var g = new ContextGraph(new ParallelHeuristics(8));
            var sources = Enumerable.Range(0, Sources).Select(_ => g.Root.FreshId()).ToArray();
            var targets = Enumerable.Range(0, Targets).Select(_ => g.Root.FreshId()).ToArray();
            g.Assert(g.Root, g.Edge, sources[0], targets[0]);
            g.Root.FlushUpdates();
            var rules = new[] { g.EdgeToPath(), g.Transitive() };
            g.Root.Saturate(rules, 10);
            g.Root.Freeze();

            void Program(EGraphContext child, int index)
            {
                foreach (var s in sources)
                    foreach (var t in targets)
                        g.Assert(child, g.Edge, s, t);

                // A short chain among the targets, different per child, so the transitive rule has work.
                for (var j = 0; j < 8; j++)
                    g.Assert(child, g.Edge, targets[(j * (index + 3)) % Targets], targets[(j * (index + 3) + index + 1) % Targets]);

                child.FlushUpdates();
                Assert.True(child.Context.Database.TotalSizeEstimate > ParallelHeuristics.DbLevelThreshold, $"the child holds {child.Context.Database.TotalSizeEstimate} rows of its own");
                var report = child.Saturate(rules, 64);
                Assert.False(report.HitLimit);
            }

            var options = new ContextOptions { Parallelism = new ParallelHeuristics(4) };
            var children = Enumerable.Range(0, Children).Select(_ => g.Root.CreateChild(options)).ToArray();
            try
            {
                Assert.All(children, c => Assert.Equal(4, c.Parallelism.DegreeOfParallelism));
                ConcurrentChildren.Run(children, (child, i) => Program(child, i));
                for (var i = 0; i < Children; i++)
                {
                    children[i].CheckInvariants();
                    using var replay = g.Root.CreateChild(new ContextOptions { Parallelism = ParallelHeuristics.Serial });
                    Program(replay, i);
                    Assert.Equal(ContextGraph.Keys(replay, g.Path), ContextGraph.Keys(children[i], g.Path));
                    Assert.Equal(ContextGraph.Keys(replay, g.Edge), ContextGraph.Keys(children[i], g.Edge));
                }
            }
            finally
            {
                foreach (var child in children)
                    child.Dispose();
            }
        }

    }

}

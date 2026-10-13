using System.Linq;

using Xunit;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// Review probe for colors M10.3 §10.9: "Under inherited heuristics a child's rule set can go parallel once its own
    /// rows pass <c>DbLevelThreshold</c>; layer creation from those tasks is safe (X37)." Exercised through the bridge's
    /// <see cref="EGraphContext.Saturate"/>, with the child's rule sets forced parallel
    /// (<see cref="Database.ParallelRuleSetsOverride"/>) so that the unit suite need not grow a child past the threshold.
    /// </summary>
    public class ChildParallelRuleSetReviewTests
    {

        /// <summary>
        /// A chain of 40 ids, half its edges at the root and half in a child: the child's transitive closure, run with
        /// parallel rule sets (its layers for <c>path</c> created by the parallel action tasks), equals the serial run.
        /// The child inherits a writable root's heuristics, or is given its own under a frozen root.
        /// </summary>
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void AChildsParallelRuleSetsEqualItsSerialRun(bool freezeRoot)
        {
            var serial = Run(parallel: false, freezeRoot);
            var parallel = Run(parallel: true, freezeRoot);
            Assert.Equal(40 * 39 / 2, serial.Length);
            Assert.Equal(serial, parallel);
        }

        /// <summary>
        /// Runs the program and returns the child's <c>path</c> keys.
        /// </summary>
        static string[] Run(bool parallel, bool freezeRoot)
        {
            var g = new ContextGraph(parallel ? new ParallelHeuristics(4) : ParallelHeuristics.Serial);
            var ids = Enumerable.Range(0, 40).Select(_ => g.Root.FreshId()).ToArray();
            for (var i = 0; i + 1 < ids.Length; i += 2)
                g.Assert(g.Root, g.Edge, ids[i], ids[i + 1]);

            g.Root.FlushUpdates();
            var rules = new[] { g.EdgeToPath(), g.Transitive() };
            g.Root.Saturate(rules, 100);
            if (freezeRoot)
                g.Root.Freeze();

            var options = freezeRoot && parallel ? new ContextOptions { Parallelism = new ParallelHeuristics(4) } : null;
            using var child = g.Root.CreateChild(options);
            Assert.Equal(parallel ? 4 : 1, child.Parallelism.DegreeOfParallelism);
            child.Context.Database.ParallelRuleSetsOverride = parallel;
            for (var i = 1; i + 1 < ids.Length; i += 2)
                g.Assert(child, g.Edge, ids[i], ids[i + 1]);

            child.FlushUpdates();
            var report = child.Saturate(rules, 200);
            Assert.False(report.HitLimit);
            child.CheckInvariants();
            return ContextGraph.Keys(child, g.Path);
        }

    }

}

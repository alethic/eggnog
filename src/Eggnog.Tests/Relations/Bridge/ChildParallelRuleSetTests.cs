using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;

using Xunit;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// A child's parallel rule sets. Under inherited heuristics a child's rule set can go parallel once its own rows
    /// pass <see cref="ParallelHeuristics.DbLevelThreshold"/>, and the layers its parallel action tasks create are
    /// created safely. Exercised through the bridge's <see cref="EGraphContext.Saturate"/>: with the child's rule sets
    /// forced parallel (<see cref="Database.ParallelRuleSetsOverride"/>), so that the test need not grow a child past
    /// the threshold; and in children past the threshold run under <see cref="ConcurrentChildren"/>, whose rule sets go
    /// parallel by the heuristics inside a leased body. Each child must end equal to a serial run of its program and
    /// pass its invariant checks. That a child given the force runs its rules on two threads at once is checked by
    /// <see cref="ChildForcedParallelRuleSetTests"/>.
    /// </summary>
    public class ChildParallelRuleSetTests
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

        const int Sources = 110;

        const int Targets = 100;

        const int Children = 4;

        /// <summary>
        /// Four children of a frozen root, each with four threads (two run at a time under a budget of eight), each
        /// asserts 11,000 edges of its own and saturates <c>edge → path</c> and the transitive rule.
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

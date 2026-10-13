using System.Linq;

using Xunit;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// A child's internal parallel rule set under <see cref="ConcurrentChildren"/>, with more than
    /// <see cref="ParallelHeuristics.DbLevelThreshold"/> rows of its own, so that the child's rule sets go parallel by
    /// the heuristics (not forced, as in <see cref="ChildParallelRuleSetTests"/>) while sibling children run on other
    /// threads. Each child must equal a serial replay of its program in a fresh serial child of the same frozen root,
    /// and pass its invariant checks.
    /// </summary>
    public class ConcurrentChildParallelRuleSetTests
    {

        const int Children = 3;
        const int Triples = 3_000;

        [Fact]
        public void ChildrenOverTheThresholdRunParallelRuleSetsConcurrently()
        {
            var g = new ContextGraph(new ParallelHeuristics(4));
            var rules = new[] { g.EdgeToPath(), g.Transitive() };
            var ids = Enumerable.Range(0, Children * Triples * 3).Select(_ => g.Root.FreshId()).ToArray();
            g.Assert(g.Root, g.Edge, g.Root.FreshId(), g.Root.FreshId());
            g.Root.FlushUpdates();
            g.Root.Saturate(rules, 16);
            g.Root.Freeze();

            var options = new ContextOptions { Parallelism = new ParallelHeuristics(2) };
            var children = Enumerable.Range(0, Children).Select(_ => g.Root.CreateChild(options)).ToArray();
            var sizes = new long[Children];
            var paths = new string[Children][];
            try
            {
                ConcurrentChildren.Run(children, (child, i) =>
                {
                    Program(g, child, ids, i);
                    var report = child.Saturate(rules, 16);
                    Assert.False(report.HitLimit);
                    sizes[i] = child.Context.Database.TotalSizeEstimate;
                    child.CheckInvariants();
                    paths[i] = ContextGraph.Keys(child, g.Path);
                });

                for (var i = 0; i < Children; i++)
                {
                    Assert.True(sizes[i] > ParallelHeuristics.DbLevelThreshold, $"child {i} holds {sizes[i]} rows of its own");
                    Assert.True(children[i].Parallelism.ParallelizeDbLevelOp(sizes[i]));
                    using var serial = g.Root.CreateChild();
                    Assert.Equal(1, serial.Parallelism.DegreeOfParallelism);
                    Program(g, serial, ids, i);
                    serial.Saturate(rules, 16);
                    Assert.Equal(ContextGraph.Keys(serial, g.Path), paths[i]);
                    Assert.Equal(Triples * 3 + 1, paths[i].Length);
                }
            }
            finally
            {
                foreach (var child in children)
                    child.Dispose();
            }
        }

        /// <summary>
        /// Child <paramref name="index"/>'s program: <see cref="Triples"/> chains <c>x → y → z</c> over its own slice
        /// of the root's ids, so the closure adds one path per chain.
        /// </summary>
        static void Program(ContextGraph g, EGraphContext child, Value[] ids, int index)
        {
            var offset = index * Triples * 3;
            for (var k = 0; k < Triples; k++)
            {
                g.Assert(child, g.Edge, ids[offset + 3 * k], ids[offset + 3 * k + 1]);
                g.Assert(child, g.Edge, ids[offset + 3 * k + 1], ids[offset + 3 * k + 2]);
            }

            child.FlushUpdates();
        }

    }

}

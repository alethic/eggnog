using System;
using System.Collections.Generic;
using System.Linq;

using Xunit;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// The union-find context differential: random programs of id mints, unions, rebuilds, child creation (three deep),
    /// random freezes and disposal, with ancestors changing after their children exist, run on the engine and on an
    /// oracle at once (<see cref="UnionFindContextPair"/>): the naive <see cref="ReferenceUnionFindContext"/>, for
    /// exact canonical ids, <see cref="Context.ScanMerges{TVisitor}"/>, <see cref="Context.IsLocalId"/>, journal
    /// completeness and own displacements. The engine's invariant checker runs at every check.
    /// </summary>
    /// <remarks>
    /// Environment variables: <c>EGGNOG_CONTEXT_SEEDS</c> sets the number of seeds (400 by default);
    /// <c>EGGNOG_CONTEXT_SOAK</c> runs <see cref="Soak"/> for at least that many steps (from
    /// <c>EGGNOG_CONTEXT_SEED_BASE</c>, or a random base).
    /// </remarks>
    public class UnionFindContextDifferentialTests
    {

        /// <summary>
        /// The seeds to run: 400 by default, or as many as <c>EGGNOG_CONTEXT_SEEDS</c> says.
        /// </summary>
        public static TheoryData<int> Seeds()
        {
            var count = int.TryParse(Environment.GetEnvironmentVariable("EGGNOG_CONTEXT_SEEDS"), out var n) && n > 0 ? n : 400;
            var data = new TheoryData<int>();
            for (var seed = 0; seed < count; seed++)
                data.Add(seed);

            return data;
        }

        /// <summary>
        /// Random programs of 150 steps agree with the reference at every check.
        /// </summary>
        [Theory]
        [MemberData(nameof(Seeds))]
        public void RandomProgramsAgree(int seed)
        {
            Run(seed, 150);
        }

        /// <summary>
        /// Longer programs, where classes grow large, re-keying cascades through three levels, and frozen chains are
        /// flattened on top of each other.
        /// </summary>
        [Fact]
        public void LongProgramsAgree()
        {
            var coverage = new long[Coverage];
            for (var seed = 0; seed < 40; seed++)
                Run(100_000 + seed, 600, coverage);

            // The programs exercise what they are meant to: imports, re-keyed canonicals, own unions and frozen
            // children.
            Assert.True(coverage[0] > 0, "no context imported a parent displacement");
            Assert.True(coverage[1] > 0, "no context re-keyed a merged class");
            Assert.True(coverage[2] > 0, "no context made a union of its own");
            Assert.True(coverage[3] > 0, "no child was frozen");
        }

        /// <summary>
        /// The number of coverage counters <see cref="Run"/> accumulates: imported displacements, re-keyed members, own
        /// unions in children, and frozen children.
        /// </summary>
        const int Coverage = 4;

        /// <summary>
        /// A soak: programs of 600 steps from consecutive seeds until <c>EGGNOG_CONTEXT_SOAK</c> steps have run.
        /// </summary>
        [Fact]
        public void Soak()
        {
            var steps = long.TryParse(Environment.GetEnvironmentVariable("EGGNOG_CONTEXT_SOAK"), out var s) ? s : 0;
            Assert.SkipUnless(steps > 0, "Set EGGNOG_CONTEXT_SOAK to a number of steps to soak the union-find context differential.");
            var first = int.TryParse(Environment.GetEnvironmentVariable("EGGNOG_CONTEXT_SEED_BASE"), out var b) ? b : Random.Shared.Next(1 << 30);
            var seed = first;
            var run = 0L;
            while (run < steps)
            {
                run += Run(seed, 600);
                seed++;
            }

            TestContext.Current.SendDiagnosticMessage($"Soaked {run} steps over seeds [{first}, {seed}).");
        }

        /// <summary>
        /// Runs one program and checks every live context at the end.
        /// </summary>
        /// <param name="seed">The program's seed.</param>
        /// <param name="steps">The number of steps.</param>
        /// <param name="coverage">Accumulates the <see cref="Coverage"/> counters of every child, or
        /// <c>null</c>.</param>
        /// <returns>The number of steps run.</returns>
        static int Run(int seed, int steps, long[]? coverage = null)
        {
            var random = new Random(seed);
            var root = UnionFindContextPair.Root();
            var live = new List<UnionFindContextPair> { root };
            var minted = new Dictionary<uint, ReferenceUnionFindContext>();
            var freezeAt = random.Next(3) == 0 ? random.Next(steps / 3, steps) : -1;
            try
            {
                for (var step = 0; step < steps; step++)
                {
                    if (step == freezeAt && root.IsFrozen == false)
                        root.Freeze();

                    Step(random, live[random.Next(live.Count)], live, minted, coverage);
                }

                foreach (var pair in live)
                {
                    pair.Check(minted);
                    Count(pair, coverage);
                }
            }
            catch (Exception e)
            {
                throw new Xunit.Sdk.XunitException($"seed {seed}: {e.Message}{Environment.NewLine}Program:{Environment.NewLine}{root.Trace}", e);
            }

            return steps;
        }

        /// <summary>
        /// Adds a child's statistics to the coverage counters.
        /// </summary>
        static void Count(UnionFindContextPair pair, long[]? coverage)
        {
            if (coverage is null || pair.Parent is null)
                return;

            var stats = pair.Engine.UnionFindStatistics;
            coverage[0] += stats.ImportedDisplacements;
            coverage[1] += stats.RekeyedMembers;
            coverage[2] += stats.OwnUnions;
            coverage[3] += pair.IsFrozen ? 1 : 0;
        }

        /// <summary>
        /// One random step of <paramref name="pair"/>: a mint, a union, a child, a rebuild, a freeze, a check of every
        /// live pair, or the disposal of a leaf.
        /// </summary>
        static void Step(Random random, UnionFindContextPair pair, List<UnionFindContextPair> live, Dictionary<uint, ReferenceUnionFindContext> minted, long[]? coverage)
        {
            var roll = random.Next(100);
            var ids = pair.VisibleIds();
            uint Pick() => ids[random.Next(ids.Length)];

            if (pair.IsFrozen == false && (ids.Length < 2 || (roll < 10 && ids.Length < 24)))
            {
                minted.Add(pair.NewId(), pair.Reference);
                return;
            }

            switch (roll)
            {
                case < 50:
                    if (pair.IsFrozen == false)
                        pair.Union(Pick(), Pick());
                    break;
                case < 60:
                    if (pair.Depth < 3 && live.Count < 8)
                        live.Add(pair.CreateChild());
                    break;
                case < 72:
                    pair.Rebuild();
                    break;
                case < 76:
                    if (pair.CanFreeze && random.Next(3) == 0)
                        pair.Freeze();
                    break;
                case < 92:
                    foreach (var other in live)
                        other.Check(minted);
                    break;
                default:
                    if (pair.Parent is not null && pair.Children.Count == 0)
                    {
                        Count(pair, coverage);
                        pair.Dispose();
                        live.Remove(pair);
                    }

                    break;
            }
        }

    }

}

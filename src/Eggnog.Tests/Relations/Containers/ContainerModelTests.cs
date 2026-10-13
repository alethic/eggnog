using System;
using System.Linq;

using Xunit;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// Model-based tests of containers at the root: random programs of interning, holder rows, unions and rebuilds, run
    /// through the bridge's rebuild loop and checked against the naive congruence model of
    /// <see cref="ContainerModelHarness"/>, serially and in parallel, small (full rebuilds) and large (incremental and
    /// parallel full rebuilds).
    /// </summary>
    public class ContainerModelTests
    {

        /// <summary>
        /// The small programs: degrees of parallelism 1 and 4, twelve seeds each.
        /// </summary>
        public static TheoryData<int, int> SmallCases()
        {
            var data = new TheoryData<int, int>();
            foreach (var dop in new[] { 1, 4 })
                for (var seed = 0; seed < 12; seed++)
                    data.Add(dop, seed);

            return data;
        }

        /// <summary>
        /// The large programs: degrees of parallelism 1 and 4, three seeds each.
        /// </summary>
        public static TheoryData<int, int> LargeCases()
        {
            var data = new TheoryData<int, int>();
            foreach (var dop in new[] { 1, 4 })
                for (var seed = 0; seed < 3; seed++)
                    data.Add(dop, seed);

            return data;
        }

        static ParallelHeuristics Parallelism(int dop) => dop == 1 ? ParallelHeuristics.Serial : new ParallelHeuristics(dop);

        static uint[] Elements(Random random, ContainerModelHarness harness, int count, bool allowContainers)
        {
            var containerIds = harness.ContainerIds.ToArray();
            var result = new uint[count];
            for (var i = 0; i < count; i++)
            {
                result[i] = allowContainers && containerIds.Length > 0 && random.Next(10) < 3
                    ? containerIds[random.Next(containerIds.Length)]
                    : harness.EClasses[random.Next(harness.EClasses.Count)];
            }

            return result;
        }

        /// <summary>
        /// Small programs: nested vectors and sets over a few dozen ids, unions between e-classes, checks after random
        /// rebuilds. Every rebuild is full (few containers).
        /// </summary>
        [Theory]
        [MemberData(nameof(SmallCases))]
        public void SmallProgramsAgreeWithTheModel(int dop, int seed)
        {
            var random = new Random(seed * 7919 + dop);
            var harness = new ContainerModelHarness(Parallelism(dop));
            for (var i = 0; i < 24; i++)
                harness.NewEClass();

            for (var step = 0; step < 150; step++)
            {
                var roll = random.Next(100);
                if (roll < 30)
                    harness.Intern('v', Elements(random, harness, random.Next(0, 4), true));
                else if (roll < 45)
                    harness.Intern('s', Elements(random, harness, random.Next(1, 4), true));
                else if (roll < 50)
                    harness.Intern('b', Enumerable.Range(0, random.Next(1, 3)).Select(_ => (uint)random.Next(1_000_000, 1_000_005)).ToArray());
                else if (roll < 62 && harness.ContainerIds.Any())
                    harness.AddHolder(harness.ContainerIds.ElementAt(random.Next(harness.ContainerIds.Count())));
                else if (roll < 66)
                    harness.NewEClass();
                else if (roll < 90)
                    harness.Union(harness.EClasses[random.Next(harness.EClasses.Count)], harness.EClasses[random.Next(harness.EClasses.Count)]);
                else if (roll < 95)
                    harness.Rebuild();
                else
                    harness.Check();
            }

            harness.Check();
            Assert.True(harness.ChangingPasses > 0);
        }

        /// <summary>
        /// Large programs: over a thousand vectors, then rounds of a few unions. Serially the container rebuilds are
        /// incremental (few displaced ids against many containers); with four threads, rounds with more than one
        /// displaced id rebuild every container in parallel.
        /// </summary>
        [Theory]
        [MemberData(nameof(LargeCases))]
        public void LargeProgramsAgreeWithTheModel(int dop, int seed)
        {
            var random = new Random(seed * 104_729 + dop);
            var harness = new ContainerModelHarness(Parallelism(dop));
            for (var i = 0; i < 600; i++)
                harness.NewEClass();

            for (var i = 0; i < 1300; i++)
                harness.Intern('v', Elements(random, harness, random.Next(1, 4), i > 1000));

            for (var i = 0; i < 40; i++)
                harness.Intern('s', Elements(random, harness, random.Next(1, 4), true));

            for (var i = 0; i < 100; i++)
                harness.AddHolder(harness.ContainerIds.ElementAt(random.Next(1340)));

            harness.Check();
            VecContainer.TryRebuildCalls = 0;
            var passes = 0;
            for (var round = 0; round < 25; round++)
            {
                var unions = random.Next(1, 4);
                for (var u = 0; u < unions; u++)
                    harness.Union(harness.EClasses[random.Next(harness.EClasses.Count)], harness.EClasses[random.Next(harness.EClasses.Count)]);

                if (random.Next(3) == 0)
                    harness.Intern('v', Elements(random, harness, 2, true));

                passes += harness.Check();
            }

            Assert.True(harness.ChangingPasses > 0);

            // Serially every pass rebuilds the vectors incrementally: far fewer containers are revisited than a full
            // rebuild per pass would visit.
            if (dop == 1)
                Assert.True(VecContainer.TryRebuildCalls < passes * 1300 / 10, $"{VecContainer.TryRebuildCalls} vectors rebuilt over {passes} passes");
        }

    }

}

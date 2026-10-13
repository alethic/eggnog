using System;
using System.Collections.Generic;
using System.Linq;

using Xunit;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// The engine and the spike in lockstep over long programs (colored-contexts spec §17 M9.4 gate): 5000-step random
    /// programs without retractions (the spike has none), compared modulo renaming at every check, and with the naive
    /// reference at the end. Long enough that layers fill, hidden sets turn into bitsets, catch-ups and step 4 copy
    /// repeatedly, and children come and go many times.
    /// </summary>
    public class SpikeLockstepTests
    {

        /// <summary>
        /// The seeds: four programs by default, or as many as <c>EGGNOG_LOCKSTEP_SEEDS</c> says.
        /// </summary>
        public static TheoryData<int> Seeds()
        {
            var count = int.TryParse(Environment.GetEnvironmentVariable("EGGNOG_LOCKSTEP_SEEDS"), out var n) && n > 0 ? n : 4;
            var data = new TheoryData<int>();
            for (var seed = 0; seed < count; seed++)
                data.Add(200_000 + seed);

            return data;
        }

        /// <summary>
        /// One 5000-step program: the engine and the spike agree modulo renaming at every check, and the reference at
        /// the end; no copy is ever re-stamped (I21).
        /// </summary>
        [Theory]
        [MemberData(nameof(Seeds))]
        public void LongProgramsStayInLockstepWithTheSpike(int seed)
        {
            const int Steps = 5000;
            var random = new Random(seed);
            var root = SpikePair.Root(withSpike: true);
            root.Program.CheckFoundRows = false;
            root.Program.CompareWithReference = false;
            var live = new List<SpikePair> { root };
            var freezeAt = random.Next(2) == 0 ? random.Next(Steps / 3, Steps) : -1;
            try
            {
                for (var step = 0; step < Steps; step++)
                {
                    if (step == freezeAt && root.IsFrozen == false)
                    {
                        root.Freeze();
                        root.Check();
                    }

                    var writable = live.Where(p => p.IsFrozen == false).ToList();
                    if (writable.Count == 0)
                        break;

                    ContextDifferentialTests.Step(random, writable[random.Next(writable.Count)], live, allowChildren: true, retractions: false);
                }

                root.Program.CompareWithReference = true;
                foreach (var pair in live)
                {
                    pair.Check();
                    Assert.Equal(0, pair.Engine.Tables?.Statistics.RestampedCopies ?? 0);
                }
            }
            catch (Exception e)
            {
                var trace = root.Program.Trace;
                throw new Xunit.Sdk.XunitException($"seed {seed}: {e.Message}{Environment.NewLine}Program (last 200 lines):{Environment.NewLine}{string.Join(Environment.NewLine, trace.Split(Environment.NewLine).TakeLast(200))}", e);
            }
        }

    }

}

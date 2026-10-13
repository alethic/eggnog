using System;
using System.Collections.Generic;
using System.Linq;

using Xunit;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// The context differential over long programs: 5000-step random programs, retractions included, compared with the
    /// naive reference at every check. Long enough that layers fill, hidden sets turn into bitsets, catch-ups and the
    /// rebuild's hide-and-copy step copy repeatedly, and children come and go many times.
    /// </summary>
    public class LongContextProgramTests
    {

        /// <summary>
        /// The seeds: four programs by default, or as many as <c>EGGNOG_LONG_CONTEXT_SEEDS</c> says.
        /// </summary>
        public static TheoryData<int> Seeds()
        {
            var count = int.TryParse(Environment.GetEnvironmentVariable("EGGNOG_LONG_CONTEXT_SEEDS"), out var n) && n > 0 ? n : 4;
            var data = new TheoryData<int>();
            for (var seed = 0; seed < count; seed++)
                data.Add(200_000 + seed);

            return data;
        }

        /// <summary>
        /// One 5000-step program: the engine agrees with the reference at every check and at the end; no copy is ever
        /// re-stamped, since every writer stamps rows with its state's timestamp.
        /// </summary>
        [Theory]
        [MemberData(nameof(Seeds))]
        public void LongProgramsAgreeWithTheReference(int seed)
        {
            const int Steps = 5000;
            var random = new Random(seed);
            var root = ContextPair.Root();
            root.Program.CheckFoundRows = false;
            var live = new List<ContextPair> { root };
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

                    ContextDifferentialTests.Step(random, writable[random.Next(writable.Count)], live, allowChildren: true, retractions: true);
                }

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

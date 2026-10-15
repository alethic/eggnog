using System;
using System.Diagnostics;

using Xunit;

namespace Eggnog.Tests.Support
{

    /// <summary>
    /// The opt-in soak of the concurrency tests: each soak test runs iterations over fresh seeds for as long as
    /// <c>EGGNOG_CONCURRENT_SOAK</c> (seconds) allows, and is skipped when it is not set. Each iteration draws its whole
    /// shape (child count, thread count, programs) from its seed, so a failure names the seed and the draws, and setting
    /// <c>EGGNOG_CONCURRENT_SEED</c> to that seed with a soak of one second replays it first.
    /// </summary>
    /// <remarks>
    /// Seeds run consecutively from <c>EGGNOG_CONCURRENT_SEED</c> if set, otherwise from a random base, which the test
    /// output prints. At least one iteration runs, however short the budget.
    /// </remarks>
    internal static class ConcurrentSoak
    {

        /// <summary>
        /// The environment variable that sets the soak's budget in seconds.
        /// </summary>
        public const string SecondsVariable = "EGGNOG_CONCURRENT_SOAK";

        /// <summary>
        /// The environment variable that sets the first seed.
        /// </summary>
        public const string SeedVariable = "EGGNOG_CONCURRENT_SEED";

        /// <summary>
        /// Runs <paramref name="iteration"/> over consecutive seeds until the budget is spent, or skips the test when
        /// no budget is set. A failing iteration fails the test with its seed, its draws and how to replay it.
        /// </summary>
        /// <param name="output">The test's output, which gets the seed range and the number of iterations.</param>
        /// <param name="test">The test's name, for the messages.</param>
        /// <param name="iteration">Given a seed, returns a description of what the seed draws and the iteration
        /// itself.</param>
        public static void Run(ITestOutputHelper output, string test, Func<int, (string Draws, Action Run)> iteration)
        {
            var seconds = int.TryParse(Environment.GetEnvironmentVariable(SecondsVariable), out var s) ? s : 0;
            Assert.SkipUnless(seconds > 0, $"Set {SecondsVariable} to a number of seconds to soak {test}.");

            var start = int.TryParse(Environment.GetEnvironmentVariable(SeedVariable), out var b) ? b : Random.Shared.Next(1 << 30);
            output.WriteLine($"soaking {test} from seed {start} for {seconds} s");
            var clock = Stopwatch.StartNew();
            var seed = start;
            do
            {
                var (draws, run) = iteration(seed);
                try
                {
                    run();
                }
                catch (Exception e)
                {
                    throw new Xunit.Sdk.XunitException($"{test} failed at seed {seed} ({draws}); replay it with {SeedVariable}={seed} and {SecondsVariable}=1.{Environment.NewLine}{e}", e);
                }

                seed++;
            }
            while (clock.Elapsed.TotalSeconds < seconds);

            output.WriteLine($"seeds {start}..{seed - 1}: {seed - start} iterations");
        }

    }

}

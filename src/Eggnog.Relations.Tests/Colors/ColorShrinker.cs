using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// Shrinks a failing <see cref="ColorProgram"/> by dropping commands (colored-contexts spec §17 M10.3): chunks of
    /// halving size, keeping a removal while the program still fails, so the printed program is short enough to read.
    /// Commands that lose their context or ids are skipped by the runner, so every candidate runs.
    /// </summary>
    static class ColorShrinker
    {

        /// <summary>
        /// Shrinks <paramref name="program"/>, which failed with <paramref name="failure"/> under
        /// <paramref name="options"/>, for at most <paramref name="maxRuns"/> runs or <paramref name="maxSeconds"/>.
        /// </summary>
        /// <returns>The smallest failing program found and its failure.</returns>
        public static (ColorProgram Program, Exception Failure) Shrink(ColorProgram program, ColorRunOptions options, Exception failure, int maxRuns = 2000, int maxSeconds = 60)
        {
            var best = program;
            var bestFailure = failure;
            var clock = Stopwatch.StartNew();
            var runs = 0;
            var chunk = Math.Max(1, best.Commands.Count / 2);
            while (runs < maxRuns && clock.Elapsed.TotalSeconds < maxSeconds)
            {
                var removed = false;
                for (var start = 0; start < best.Commands.Count && runs < maxRuns && clock.Elapsed.TotalSeconds < maxSeconds;)
                {
                    var commands = new List<ColorCommand>(best.Commands);
                    commands.RemoveRange(start, Math.Min(chunk, commands.Count - start));
                    var candidate = best.With(commands);
                    runs++;
                    try
                    {
                        new ColorRunner(candidate, options).Run();
                        start += chunk;
                    }
                    catch (Exception e)
                    {
                        best = candidate;
                        bestFailure = e;
                        removed = true;
                    }
                }

                if (removed == false)
                {
                    if (chunk == 1)
                        break;

                    chunk = Math.Max(1, chunk / 2);
                }
            }

            return (best, bestFailure);
        }

    }

}

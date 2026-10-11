using System;
using System.Diagnostics;
using System.Linq;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// Shrinks a failing differential program: drops steps, then simplifies the remaining ones
    /// (<see cref="DiffStep.Simplify"/>), keeping each change after which the program still fails with the same kind
    /// of exception, until nothing more helps or the budget (runs and time) is spent.
    /// </summary>
    static class DiffShrinker
    {

        /// <summary>
        /// Shrinks <paramref name="program"/>, which failed with <paramref name="failure"/>.
        /// </summary>
        /// <param name="program">The failing program.</param>
        /// <param name="profile">Its profile.</param>
        /// <param name="failure">Its failure.</param>
        /// <param name="maxRuns">The most programs to try.</param>
        /// <param name="maxSeconds">The most time to spend.</param>
        /// <returns>The smallest failing program found and its failure.</returns>
        public static (DiffProgram Program, Exception Failure) Shrink(DiffProgram program, DiffProfile profile, Exception failure, int maxRuns = 4000, int maxSeconds = 120)
        {
            var kind = failure.GetType();
            var clock = Stopwatch.StartNew();
            var runs = 0;
            bool Spent() => runs >= maxRuns || clock.Elapsed.TotalSeconds > maxSeconds;

            var progress = true;
            while (progress && Spent() == false)
            {
                progress = false;
                for (var i = program.Steps.Count - 1; i >= 0 && Spent() == false; i--)
                {
                    var candidate = program with { Steps = program.Steps.Where((_, j) => j != i).ToArray() };
                    runs++;
                    if (Fails(candidate, profile, kind) is { } f)
                    {
                        (program, failure) = (candidate, f);
                        progress = true;
                    }
                }

                for (var i = 0; i < program.Steps.Count && Spent() == false; i++)
                {
                    var simplified = true;
                    while (simplified && Spent() == false)
                    {
                        simplified = false;
                        foreach (var simpler in program.Steps[i].Simplify(program))
                        {
                            var candidate = program with { Steps = program.Steps.Select((s, j) => j == i ? simpler : s).ToArray() };
                            runs++;
                            if (Fails(candidate, profile, kind) is { } f)
                            {
                                (program, failure) = (candidate, f);
                                simplified = true;
                                progress = true;
                                break;
                            }

                            if (Spent())
                                break;
                        }
                    }
                }
            }

            return (program, failure);
        }

        /// <summary>
        /// Runs <paramref name="program"/> and returns its failure if it is of type <paramref name="kind"/>.
        /// </summary>
        public static Exception? Fails(DiffProgram program, DiffProfile profile, Type kind)
        {
            try
            {
                new DiffRunner(program, profile, new DiffStats()).Run();
                return null;
            }
            catch (Exception e) when (e.GetType() == kind)
            {
                return e;
            }
            catch (Exception)
            {
                return null;
            }
        }

    }

}

using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;

using Eggnog.Relations.Benchmarks;

namespace Eggnog.Benchmarks.Library
{

    /// <summary>
    /// The console app's mode <c>--math-microbenchmark</c>: runs <see cref="MathMicrobenchmarkProgram"/> without
    /// BenchmarkDotNet, the way <c>egglog -j n tests/math-microbenchmark.egg</c> runs the file, and prints the table
    /// sizes as its <c>(print-size)</c> does, so the two can be run side by side.
    /// </summary>
    /// <remarks>
    /// Each repetition builds a new graph, runs it and prints its sizes, then <c>Total time: {:?}</c>, timed from before
    /// the graph's creation to after the sizes are read; the check (<see cref="MathMicrobenchmarkProgram.Verify"/>)
    /// follows, outside the time. Between repetitions the graph is released and the heap collected, outside the time.
    /// The first repetition includes JIT compilation; egglog's process time includes parsing the file and starting the
    /// process.
    /// </remarks>
    internal static class MathMicrobenchmarkRunner
    {

        /// <summary>
        /// The command-line option that selects this mode.
        /// </summary>
        public const string Option = "--math-microbenchmark";

        /// <summary>
        /// Runs the mode with the arguments after <see cref="Option"/>: <c>--threads n</c> (egglog's <c>-j</c>, default
        /// 1) and <c>--repetitions n</c> (default 2).
        /// </summary>
        /// <param name="args">The arguments after <see cref="Option"/>.</param>
        /// <returns>0 when every repetition matched egglog's sizes; 1 when one did not; 2 for a bad command line.</returns>
        public static int Run(ReadOnlySpan<string> args)
        {
            var threads = 1;
            var repetitions = 2;
            for (var i = 0; i < args.Length; i += 2)
            {
                var valid = i + 1 < args.Length && int.TryParse(args[i + 1], NumberStyles.None, CultureInfo.InvariantCulture, out var value) && value >= 1;
                if (valid && args[i] == "--threads")
                    threads = int.Parse(args[i + 1], CultureInfo.InvariantCulture);
                else if (valid && args[i] == "--repetitions")
                    repetitions = int.Parse(args[i + 1], CultureInfo.InvariantCulture);
                else
                {
                    Console.Error.WriteLine($"usage: Eggnog.Benchmarks {Option} [--threads <n>] [--repetitions <n>]");
                    return 2;
                }
            }

            using var stdout = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { NewLine = "\n", AutoFlush = true };
            for (var i = 0; i < repetitions; i++)
            {
                var error = RunOnce(stdout, threads);
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();

                if (error != null)
                {
                    Console.Error.WriteLine(error);
                    return 1;
                }
            }

            return 0;
        }

        /// <summary>
        /// One repetition: build, run, print the sizes and the time, then check. Not inlined, so the graph is
        /// unreachable once it returns.
        /// </summary>
        /// <param name="stdout">Where the lines go.</param>
        /// <param name="threads">The graph's degree of parallelism.</param>
        /// <returns>The failed check's message; <c>null</c> when the result matched egglog's.</returns>
        [MethodImpl(MethodImplOptions.NoInlining)]
        static string? RunOnce(TextWriter stdout, int threads)
        {
            var start = Stopwatch.GetTimestamp();
            var program = new MathMicrobenchmarkProgram(threads);
            var report = program.Run();
            program.WriteSizes(stdout);
            stdout.WriteLine($"Passes: {report.Iterations} (saturated={(report.Saturated ? "true" : "false")})");
            stdout.WriteLine($"Total time: {RustDuration.Format(RustDuration.ElapsedNanoseconds(start))}");

            try
            {
                program.Verify(report);
                return null;
            }
            catch (InvalidOperationException e)
            {
                return e.Message;
            }
        }

    }

}

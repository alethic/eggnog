using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;

namespace Eggnog.Relations.Benchmarks
{

    /// <summary>
    /// The console app's example mode, <c>--bridge-example math</c> or <c>--bridge-example ac</c>: runs
    /// <see cref="BridgeMathProgram"/> or <see cref="BridgeAcProgram"/> the way the Rust <c>main</c> of egglog-bridge's
    /// <c>examples/math.rs</c> or <c>examples/ac.rs</c> runs it, without BenchmarkDotNet, and prints the same lines, so
    /// the output can be compared side by side with <c>math.exe</c> or <c>ac.exe</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Each program runs <see cref="Repetitions"/> times, as both <c>main</c>s do (<c>for _ in 0..2</c>), each repetition
    /// on a new e-graph timed from before the e-graph's creation. The lines are the Rust's: <c>math</c> prints one
    /// <c>Iteration {i} finished, duration={:?} (saturated={})</c> per iteration, then <c>Function {name} has size {n}</c>
    /// per table and <c>Total time: {:?}</c>; <c>ac</c> prints <c>Finished iteration {i} after {:?}</c> per iteration and
    /// <c>Time elapsed: {:?}</c>. Durations are formatted as Rust's <c>Debug</c> formats them (<see cref="RustDuration"/>),
    /// and the lines are written to standard output as UTF-8 ended by <c>\n</c>, as Rust's <c>println!</c> writes them, one
    /// flush per line.
    /// </para>
    /// <para>
    /// Each repetition's result is checked: <c>math</c> after its <c>Total time</c> line
    /// (<see cref="BridgeMathProgram.Verify"/>, outside the time, since <c>math.rs</c> checks nothing), <c>ac</c> before its
    /// <c>Time elapsed</c> line, inside the time, where <c>ac.rs</c> asserts: the rules change nothing on the empty
    /// database (in <see cref="BridgeAcProgram"/>'s constructor) and the saturated result
    /// (<see cref="BridgeAcProgram.Verify"/>). A failed check is reported on standard error and ends the run with exit
    /// code 1. Between repetitions the e-graph is released and the heap collected, outside the time, as the Rust drops
    /// the e-graph after its timer stops ("We don't include dropping the egraph in the time"). The first repetition
    /// includes JIT compilation, which the Rust does not have.
    /// </para>
    /// </remarks>
    internal static class BridgeExampleRunner
    {

        /// <summary>
        /// The command-line option that selects this mode; the next argument names the program.
        /// </summary>
        public const string Option = "--bridge-example";

        /// <summary>
        /// The repetitions of each program: both Rust <c>main</c>s loop <c>for _ in 0..2</c>.
        /// </summary>
        const int Repetitions = 2;

        /// <summary>
        /// Runs the program named by <paramref name="args"/>, the arguments after <see cref="Option"/>.
        /// </summary>
        /// <param name="args"><c>math</c> or <c>ac</c>.</param>
        /// <returns>0 when every repetition's result matched egglog's; 1 when one did not; 2 for a bad command line.</returns>
        public static int Run(ReadOnlySpan<string> args)
        {
            if (args.Length != 1 || (args[0] != "math" && args[0] != "ac"))
            {
                Console.Error.WriteLine($"usage: Eggnog.Benchmarks {Option} math|ac");
                return 2;
            }

            var isMath = args[0] == "math";
            using var stdout = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { NewLine = "\n", AutoFlush = true };
            for (var i = 0; i < Repetitions; i++)
            {
                var error = isMath ? RunMath(stdout) : RunAc(stdout);

                // Drop the e-graph outside the time, as the Rust does at the end of each repetition.
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
        /// One repetition of <c>math.rs</c>'s loop body: build, seed and iterate, then the size lines and
        /// <c>Total time</c>, then the check. Not inlined, so the e-graph is unreachable once it returns.
        /// </summary>
        /// <param name="stdout">Where the lines go.</param>
        /// <returns>The failed check's message; <c>null</c> when the result matched egglog's.</returns>
        [MethodImpl(MethodImplOptions.NoInlining)]
        static string? RunMath(TextWriter stdout)
        {
            var start = Stopwatch.GetTimestamp();
            var program = new BridgeMathProgram();
            var (iterations, saturated) = program.Run(stdout);
            program.WriteTableSizes(stdout);
            stdout.WriteLine($"Total time: {RustDuration.Format(RustDuration.ElapsedNanoseconds(start))}");

            try
            {
                program.Verify(iterations, saturated);
                return null;
            }
            catch (InvalidOperationException e)
            {
                return e.Message;
            }
        }

        /// <summary>
        /// One repetition of <c>ac.rs</c>'s loop body: build (with the check that the rules change nothing on the empty
        /// database), load, saturate and check, then <c>Time elapsed</c> (not printed when either check fails, as
        /// <c>ac.rs</c> panics first). Not inlined, so the e-graph is unreachable once it returns.
        /// </summary>
        /// <param name="stdout">Where the lines go.</param>
        /// <returns>The failed check's message; <c>null</c> when the result matched egglog's.</returns>
        [MethodImpl(MethodImplOptions.NoInlining)]
        static string? RunAc(TextWriter stdout)
        {
            var start = Stopwatch.GetTimestamp();
            try
            {
                var program = new BridgeAcProgram();
                var iterations = program.Saturate(stdout);
                program.Verify(iterations);
            }
            catch (InvalidOperationException e)
            {
                // ac.rs panics on either failed assertion before the time line.
                return e.Message;
            }

            stdout.WriteLine($"Time elapsed: {RustDuration.Format(RustDuration.ElapsedNanoseconds(start))}");
            return null;
        }

    }

}

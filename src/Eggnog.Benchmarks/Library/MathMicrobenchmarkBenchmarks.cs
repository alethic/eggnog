using System;

using BenchmarkDotNet.Attributes;

namespace Eggnog.Benchmarks.Library
{

    /// <summary>
    /// egglog's <c>tests/math-microbenchmark.egg</c> through the library's public API
    /// (<see cref="MathMicrobenchmarkProgram"/>): one invocation declares the theory, adds the seed expressions, runs
    /// <c>(run 11)</c> and reads the table sizes. The counterpart is <c>egglog -j n tests/math-microbenchmark.egg</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="DegreeOfParallelism"/> is the graph's <see cref="EGraphOptions.DegreeOfParallelism"/>: 1, the
    /// library's default and the egglog command line's, and 8, every logical processor of the four-core, eight-thread
    /// machine the campaign runs on.
    /// </para>
    /// <para>
    /// Each invocation checks its result (<see cref="MathMicrobenchmarkProgram.Verify"/>): every table at the size egglog
    /// prints, so a broken library fails the run instead of producing a number. The graph stays referenced until
    /// <see cref="IterationCleanupAttribute"/>, so it is collected by BenchmarkDotNet's forced collection before the next
    /// iteration, not during a measurement. One invocation per iteration.
    /// </para>
    /// <para>
    /// The class is not sealed because BenchmarkDotNet generates a class that derives from it.
    /// </para>
    /// </remarks>
    [InvocationCount(1)]
    public class MathMicrobenchmarkBenchmarks
    {

        MathMicrobenchmarkProgram? program;

        /// <summary>
        /// Gets or sets the graph's degree of parallelism: egglog's <c>-j</c>.
        /// </summary>
        [Params(1, 8)]
        public int DegreeOfParallelism { get; set; }

        /// <summary>
        /// Releases the graph of the last invocation, outside the measurement.
        /// </summary>
        [IterationCleanup]
        public void IterationCleanup()
        {
            GC.KeepAlive(program);
            program = null;
        }

        /// <summary>
        /// One run of the file: declare, seed, run, and the size check.
        /// </summary>
        /// <returns>The passes the run made.</returns>
        /// <exception cref="InvalidOperationException">The result differs from egglog's.</exception>
        [Benchmark]
        public int Math()
        {
            var math = new MathMicrobenchmarkProgram(DegreeOfParallelism);
            var report = math.Run();
            math.Verify(report);
            program = math;
            return report.Iterations;
        }

    }

}

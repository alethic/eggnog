using System;

using BenchmarkDotNet.Attributes;

namespace Eggnog.Relations.Benchmarks
{

    /// <summary>
    /// egglog-bridge's <c>examples/math.rs</c> (<see cref="BridgeMathProgram"/>): one invocation is one repetition of
    /// the Rust <c>main</c>'s loop body, measured over exactly what <c>math.rs</c> times between <c>Instant::now()</c>
    /// and its <c>Total time</c> line.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Measured: creating the e-graph, registering the base types, declaring the 13 tables, building the 24 rules, adding
    /// the seed expressions, the 12 iterations, and reading the table sizes (which <c>math.rs</c> prints inside its
    /// timer). Not measured: the per-iteration and size lines <c>math.rs</c> prints, and dropping the e-graph, which
    /// <c>math.rs</c> also leaves out of its time. The e-graph stays referenced until
    /// <see cref="IterationCleanupAttribute"/>, so it is collected by BenchmarkDotNet's forced collection before the next
    /// iteration, not during a measurement.
    /// </para>
    /// <para>
    /// Each invocation checks its result (<see cref="BridgeMathProgram.Verify"/>): 12 iterations without saturating and
    /// every table at the size <c>math.exe</c> prints, so a broken engine fails the run instead of producing a number.
    /// One invocation per iteration; an invocation takes seconds and builds about 16 million rows.
    /// </para>
    /// <para>
    /// The class is not sealed because BenchmarkDotNet generates a class that derives from it.
    /// </para>
    /// </remarks>
    [InvocationCount(1)]
    public class BridgeMathBenchmarks
    {

        BridgeMathProgram? program;

        /// <summary>
        /// Releases the e-graph of the last invocation, outside the measurement.
        /// </summary>
        [IterationCleanup]
        public void IterationCleanup()
        {
            GC.KeepAlive(program);
            program = null;
        }

        /// <summary>
        /// One run of <c>math.rs</c>: build, seed, iterate, read the sizes; then the check.
        /// </summary>
        /// <returns>The number of iterations run.</returns>
        /// <exception cref="InvalidOperationException">The result differs from egglog's.</exception>
        [Benchmark]
        public int Math()
        {
            var math = new BridgeMathProgram();
            var (iterations, saturated) = math.Run(null);
            math.Verify(iterations, saturated);
            program = math;
            return iterations;
        }

    }

}

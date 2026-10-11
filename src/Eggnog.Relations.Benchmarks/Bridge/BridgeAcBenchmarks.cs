using System;

using BenchmarkDotNet.Attributes;

namespace Eggnog.Relations.Benchmarks
{

    /// <summary>
    /// egglog-bridge's <c>examples/ac.rs</c> (<see cref="BridgeAcProgram"/>): one invocation is one repetition of the
    /// Rust <c>main</c>'s loop body, measured over exactly what <c>ac.rs</c> times between <c>Instant::now()</c> and its
    /// <c>Time elapsed</c> line.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Measured: creating the e-graph, registering the base type, declaring both tables, building both rules, the run on
    /// the empty database, loading the 13 numbers and both sums, the saturation loop (10 iterations; <c>ac.exe</c> runs
    /// an eleventh that finds nothing, see <see cref="BridgeAcProgram"/>), and the check that the sums are equal
    /// (<c>ac.rs</c>'s <c>assert_eq!</c>, inside its timer). Not measured: the per-iteration lines <c>ac.rs</c> prints,
    /// and dropping the e-graph, which <c>ac.rs</c> also leaves out of its time. The e-graph stays referenced until
    /// <see cref="IterationCleanupAttribute"/>, so it is collected by BenchmarkDotNet's forced collection before the next
    /// iteration, not during a measurement.
    /// </para>
    /// <para>
    /// Each invocation checks its result (<see cref="BridgeAcProgram.Verify"/>): the sums equal, the iteration count and
    /// the saturated table sizes, so a broken engine fails the run instead of producing a number. One invocation per
    /// iteration; an invocation takes seconds and builds about 1.6 million <c>add</c> rows.
    /// </para>
    /// <para>
    /// The class is not sealed because BenchmarkDotNet generates a class that derives from it.
    /// </para>
    /// </remarks>
    [InvocationCount(1)]
    public class BridgeAcBenchmarks
    {

        BridgeAcProgram? program;

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
        /// One run of <c>ac.rs</c>: build, load, saturate, and the equality check.
        /// </summary>
        /// <returns>The number of iterations run.</returns>
        /// <exception cref="InvalidOperationException">The result differs from egglog's.</exception>
        [Benchmark]
        public int Ac()
        {
            var ac = new BridgeAcProgram();
            var iterations = ac.Saturate(null);
            ac.Verify(iterations);
            program = ac;
            return iterations;
        }

    }

}

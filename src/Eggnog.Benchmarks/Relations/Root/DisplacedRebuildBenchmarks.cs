using System;

using BenchmarkDotNet.Attributes;

namespace Eggnog.Relations.Benchmarks
{

    /// <summary>
    /// A batch of unions merged into a <see cref="DisplacedTable"/>, then the bridge's native rebuild loop
    /// (<see cref="Database.Rebuild"/>) over the function tables until congruence closes, on a term graph of 20,000
    /// and 200,000 rows, serially and in parallel. Part of the root benchmark set that colored contexts are gated on
    /// (colored-contexts spec §17, M9.0): it exercises the union-find merge (and its path compression), the table
    /// rebuilds through <see cref="Database.ApplyRebuild"/> (incremental through the rebuild index where the delta is
    /// small, full scans elsewhere) and the congruence merges they cause.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The term graph (<see cref="TermGraphWorkload"/>, from a fixed seed) has <see cref="Rows"/> rows in a
    /// <see cref="TermGraph"/>: nested <c>pair</c> and <c>wrap</c> constructors and a <c>low</c> lattice. The
    /// invocation stages <see cref="TermGraphWorkload.UnionsFor"/> unions of leaves (one per fifty rows), merges them,
    /// and rebuilds until a pass changes nothing; the rebuild rewrites every row mentioning a displaced id, and rows
    /// that collide merge, which stages further unions, so the closure cascades over several passes.
    /// </para>
    /// <para>
    /// A fresh e-graph is built and loaded before every invocation (<see cref="IterationSetupAttribute"/>, one
    /// invocation per iteration). After each iteration the e-graph is checked against a naive congruence closure of
    /// the same input: every id's canonical id, the union-find's size, and every table's live rows, so a broken engine
    /// fails the run instead of producing a number.
    /// </para>
    /// <para>
    /// The class is not sealed because BenchmarkDotNet generates a class that derives from it.
    /// </para>
    /// </remarks>
    [InvocationCount(1)]
    public class DisplacedRebuildBenchmarks : IItemsPerInvocation
    {

        /// <summary>
        /// The seed every workload is generated from.
        /// </summary>
        public const int Seed = 0x5EED;

        TermGraphWorkload? workload;
        TermGraph? graph;
        bool ran;

        /// <summary>
        /// Gets or sets the number of function rows in the term graph.
        /// </summary>
        [Params(20_000, 200_000)]
        public int Rows { get; set; }

        /// <summary>
        /// Gets or sets whether the merge and the rebuild run on the serial or the parallel paths.
        /// </summary>
        [Params(RunMode.Serial, RunMode.Parallel)]
        public RunMode Mode { get; set; }

        /// <summary>
        /// Generates the term graph, its unions and the reference closure.
        /// </summary>
        [GlobalSetup]
        public void GlobalSetup() => workload = TermGraphWorkload.Generate(Rows, Seed);

        /// <summary>
        /// Builds and loads the e-graph the next invocation unions and rebuilds.
        /// </summary>
        [IterationSetup]
        public void IterationSetup()
        {
            var fresh = new TermGraph(Mode);
            workload!.Load(fresh);
            graph = fresh;
        }

        /// <summary>
        /// Checks the e-graph against the reference closure, and releases it. An iteration that did not run the
        /// benchmark is not checked.
        /// </summary>
        /// <exception cref="InvalidOperationException">The e-graph differs from the reference closure.</exception>
        [IterationCleanup]
        public void IterationCleanup()
        {
            var result = graph!;
            var check = ran;
            graph = null;
            ran = false;
            if (check)
                workload!.Verify(result);
        }

        /// <summary>
        /// Merges the union batch into the union-find and rebuilds the function tables until congruence closes.
        /// </summary>
        /// <returns>The number of rebuild passes.</returns>
        [Benchmark]
        public int UnionAndRebuild()
        {
            var passes = workload!.UnionAndRebuild(graph!);
            ran = true;
            return passes;
        }

        /// <inheritdoc />
        /// <remarks>
        /// The unions of the batch.
        /// </remarks>
        long IItemsPerInvocation.ItemsPerInvocation(string benchmarkMethod) =>
            benchmarkMethod == nameof(UnionAndRebuild) ? TermGraphWorkload.UnionsFor(Rows) : 0;

    }

}

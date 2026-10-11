using System;

using BenchmarkDotNet.Attributes;

namespace Eggnog.Relations.Benchmarks
{

    /// <summary>
    /// Whole programs on <see cref="Database.RunRuleSet"/>: the AC-saturation and math e-graph programs of the
    /// executor tests (<see cref="AcSaturationWorkload"/>, <see cref="MathWorkload"/>, both over a
    /// <see cref="MathEgraph"/>), at about 20,000 and 200,000 rows, serially and in parallel. Part of the root benchmark
    /// set that colored contexts are gated on (colored-contexts spec §17, M9.0): it exercises header evaluation, the
    /// trie, the probers, the join kernels, the action interpreter and <see cref="Database.MergeAll"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="AcSaturation"/> measures the whole saturation loop of egglog's <c>ac_test_inner</c>: AC rule sets
    /// over the recent timestamp range, rule-based rebuilds, repeated until nothing changes; every rule set is built
    /// and run inside the measurement, as the program does. The leaves are chosen so the saturated <c>add</c> table
    /// is nearest <see cref="Rows"/> (<see cref="AcSaturationWorkload.LeavesFor"/>: 9 leaves and 18,660 rows, 11
    /// leaves and 173,052 rows). <see cref="BasicMath"/> measures one run of egglog's <c>basic_query</c> rule over
    /// <see cref="Rows"/> rows, its rule set built before the measurement.
    /// </para>
    /// <para>
    /// A fresh fixture is built and loaded before every invocation (<see cref="IterationSetupAttribute"/>, one invocation per
    /// iteration); workloads come from a fixed seed. After each iteration the result is checked: the AC e-graph must
    /// be the saturated one (row and class counts, canonical rows, equal sums), and the math run must match the
    /// reference model's matches, rows and unions, so a broken engine fails the run instead of producing a number.
    /// </para>
    /// <para>
    /// The class is not sealed because BenchmarkDotNet generates a class that derives from it.
    /// </para>
    /// </remarks>
    [InvocationCount(1)]
    public class RuleSetRunBenchmarks : IItemsPerInvocation
    {

        /// <summary>
        /// The seed every workload is generated from.
        /// </summary>
        public const int Seed = 0x5EED;

        AcSaturationWorkload? ac;
        MathWorkload? math;
        MathEgraph? graph;
        (uint LeftRoot, uint RightRoot) roots;
        RuleSet? ruleSet;
        RuleSetReport? report;
        bool ran;

        /// <summary>
        /// Gets or sets the size of the workload in rows: the saturated <c>add</c> table's size for
        /// <see cref="AcSaturation"/> (to the nearest size the AC workload has), the rows loaded for
        /// <see cref="BasicMath"/>.
        /// </summary>
        [Params(20_000, 200_000)]
        public int Rows { get; set; }

        /// <summary>
        /// Gets or sets whether the programs run on the serial or the parallel paths.
        /// </summary>
        [Params(RunMode.Serial, RunMode.Parallel)]
        public RunMode Mode { get; set; }

        /// <summary>
        /// Chooses the number of AC leaves for <see cref="Rows"/>.
        /// </summary>
        [GlobalSetup(Target = nameof(AcSaturation))]
        public void SetupAcSaturation() => ac = new AcSaturationWorkload(AcSaturationWorkload.LeavesFor(Rows));

        /// <summary>
        /// Generates the math e-graph and its reference model.
        /// </summary>
        [GlobalSetup(Target = nameof(BasicMath))]
        public void SetupBasicMath() => math = new MathWorkload(Rows, Seed);

        /// <summary>
        /// Builds a fresh fixture and loads the two sums the next invocation saturates.
        /// </summary>
        [IterationSetup(Target = nameof(AcSaturation))]
        public void IterationSetupAcSaturation()
        {
            graph = new MathEgraph(Mode);
            roots = ac!.Load(graph);
        }

        /// <summary>
        /// Builds a fresh fixture, loads the math e-graph and builds the rule set the next invocation runs.
        /// </summary>
        [IterationSetup(Target = nameof(BasicMath))]
        public void IterationSetupBasicMath()
        {
            graph = new MathEgraph(Mode);
            ruleSet = math!.Prepare(graph);
        }

        /// <summary>
        /// Checks that the invocation saturated the e-graph, and releases the fixture. An iteration that did not run the
        /// benchmark is not checked.
        /// </summary>
        /// <exception cref="InvalidOperationException">The e-graph is not the saturated one.</exception>
        [IterationCleanup(Target = nameof(AcSaturation))]
        public void IterationCleanupAcSaturation()
        {
            var fixture = graph!;
            var check = ran;
            graph = null;
            ran = false;
            if (check)
                ac!.Verify(fixture, roots);
        }

        /// <summary>
        /// Checks the run against the reference model, and releases the fixture and the rule set. An iteration that did
        /// not run the benchmark is not checked.
        /// </summary>
        /// <exception cref="InvalidOperationException">The run disagrees with the reference model.</exception>
        [IterationCleanup(Target = nameof(BasicMath))]
        public void IterationCleanupBasicMath()
        {
            var fixture = graph!;
            var result = report;
            var check = ran;
            ruleSet?.Dispose();
            ruleSet = null;
            graph = null;
            report = null;
            ran = false;
            if (check)
                math!.Verify(fixture, result!);
        }

        /// <summary>
        /// egglog's <c>ac_test_inner</c> saturation loop: AC rules and rule-based rebuilds until nothing changes.
        /// </summary>
        /// <returns>The number of AC iterations.</returns>
        [Benchmark]
        public int AcSaturation()
        {
            var iterations = ac!.Saturate(graph!);
            ran = true;
            return iterations;
        }

        /// <summary>
        /// egglog's <c>basic_query</c>: one run of <c>Add(x, y, z), Num(a, x), Num(b, y) =&gt; Num(a + b, z)</c>,
        /// merge included.
        /// </summary>
        /// <returns>Whether the run changed anything.</returns>
        [Benchmark]
        public bool BasicMath()
        {
            report = graph!.Db.RunRuleSet(ruleSet!);
            ran = true;
            return report.Changed;
        }

        /// <inheritdoc />
        /// <remarks>
        /// The rows of the saturated <c>add</c> table for <see cref="AcSaturation"/>; the matches (one per <c>add</c>
        /// row) for <see cref="BasicMath"/>.
        /// </remarks>
        long IItemsPerInvocation.ItemsPerInvocation(string benchmarkMethod) => benchmarkMethod switch
        {
            nameof(AcSaturation) => AcSaturationWorkload.SaturatedRows(AcSaturationWorkload.LeavesFor(Rows)),
            nameof(BasicMath) => Rows - Rows / 2,
            _ => 0,
        };

    }

}

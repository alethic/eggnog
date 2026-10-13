using System;

using BenchmarkDotNet.Attributes;

namespace Eggnog.Relations.Benchmarks
{

    /// <summary>
    /// A rule run whose work is action batches dominated by <c>LookupOrInsertDefault</c> and <c>Lookup</c>, writing a
    /// function whose merge calls
    /// <see cref="ExecutionState.PredictRow(TableId, ReadOnlySpan{uint}, ReadOnlySpan{MergeVal}, Span{uint})"/>, for
    /// 20,000 and 200,000 matches, serially and in parallel. Part of the root benchmark set, which measures the root on
    /// the engine paths colored contexts extend, so that a cost contexts add to the root shows as a regression: it
    /// exercises the action interpreter's dispatch, the vectorized lookups, the predicted-value cache, buffer creation
    /// for staging, <see cref="ExecutionState.ReadCounter"/>, and the prediction a bridge merge function makes once per
    /// merged row.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The program (<see cref="ActionLookupWorkload"/> over an <see cref="ActionLookupProgram"/>, from a fixed seed)
    /// scans <c>driver</c>, a single-atom join, and per match runs one <c>ReadCounter</c> per batch, two
    /// <c>LookupOrInsertDefault</c>s on <c>leaf</c> (hits, misses that stage predicted rows, and repeated misses that
    /// hit the prediction), one <c>Lookup</c> on <c>weight</c> (which drops the lanes of new leaves) and one
    /// <c>Insert</c> into <c>best</c>, whose merge with the existing row calls <c>combine(old, new)</c> through
    /// <see cref="FunctionMerge"/>. The run includes the merge.
    /// </para>
    /// <para>
    /// A fresh database is loaded and the rule set built before every invocation (<see cref="IterationSetupAttribute"/>,
    /// one invocation per iteration). After each iteration the run is checked against the reference model
    /// (<see cref="ActionLookupWorkload.Verify"/>): the matches, every table's live row count, and the values the
    /// merges must leave: the loaded rows unchanged, each new leaf holding a fresh canonical id, one <c>combine</c> row
    /// per merged <c>best</c> key with its second key in the class of the driver's leaf, and every <c>best</c> value
    /// equal to the output of its merge's <c>combine</c> row (or unchanged where no merge changes it). So a broken
    /// engine, including a merge path that drops or misstores the merge function's output, fails the run instead of
    /// producing a number.
    /// </para>
    /// <para>
    /// The class is not sealed because BenchmarkDotNet generates a class that derives from it.
    /// </para>
    /// </remarks>
    [InvocationCount(1)]
    public class ActionLookupBenchmarks : IItemsPerInvocation
    {

        /// <summary>
        /// The seed every workload is generated from.
        /// </summary>
        public const int Seed = 0x5EED;

        ActionLookupWorkload? workload;
        ActionLookupProgram? program;
        RuleSet? ruleSet;
        RuleSetReport? report;
        bool ran;

        /// <summary>
        /// Gets or sets the number of keys, which is also the number of the join's matches.
        /// </summary>
        [Params(20_000, 200_000)]
        public int Rows { get; set; }

        /// <summary>
        /// Gets or sets whether the rule runs on the serial or the parallel executor.
        /// </summary>
        [Params(RunMode.Serial, RunMode.Parallel)]
        public RunMode Mode { get; set; }

        /// <summary>
        /// Generates the input and the reference model.
        /// </summary>
        [GlobalSetup]
        public void GlobalSetup() => workload = new ActionLookupWorkload(Rows, Seed);

        /// <summary>
        /// Loads a fresh database and builds the rule set the next invocation runs.
        /// </summary>
        [IterationSetup]
        public void IterationSetup()
        {
            var fresh = new ActionLookupProgram(Mode);
            ruleSet = workload!.Prepare(fresh);
            program = fresh;
        }

        /// <summary>
        /// Checks the run against the reference model, and releases the database and the rule set. An iteration that
        /// did not run the benchmark is not checked.
        /// </summary>
        /// <exception cref="InvalidOperationException">The run disagrees with the reference model.</exception>
        [IterationCleanup]
        public void IterationCleanup()
        {
            var result = program!;
            var runReport = report;
            var check = ran;
            ruleSet?.Dispose();
            ruleSet = null;
            program = null;
            report = null;
            ran = false;
            if (check)
                workload!.Verify(result, runReport!);
        }

        /// <summary>
        /// Runs the rule, merge included.
        /// </summary>
        /// <returns>Whether the run changed anything.</returns>
        [Benchmark]
        public bool Run()
        {
            report = ActionLookupWorkload.Run(program!, ruleSet!);
            ran = true;
            return report.Changed;
        }

        /// <inheritdoc />
        /// <remarks>
        /// The join's matches, each of which runs the actions: one per <c>driver</c> row.
        /// </remarks>
        long IItemsPerInvocation.ItemsPerInvocation(string benchmarkMethod) => benchmarkMethod == nameof(Run) ? Rows : 0;

    }

}

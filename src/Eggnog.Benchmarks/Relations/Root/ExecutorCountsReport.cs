using System;

namespace Eggnog.Relations.Benchmarks
{

    /// <summary>
    /// The console app's executor-counts mode, <c>--executor-counts</c>: runs <see cref="RuleSetRunBenchmarks.BasicMath"/>'s
    /// workload (<see cref="MathWorkload"/>) at the benchmark's sizes and modes, and prints the executor's counters
    /// (<see cref="ExecutorCounters"/>) for each run's rule execution and its merge separately: the execution states
    /// cloned by site, the mutation buffers they made, and the first allocations and the growths of their buffer maps.
    /// It also prints the bytes one clone allocates, alone and with the buffer of its first write. Counts and bytes do
    /// not depend on the machine's load, so they need no idle machine; times are not measured.
    /// </summary>
    /// <remarks>
    /// The counters are a switch read once per process, so the mode needs <c>EGGNOG_COUNTERS=1</c> in the environment
    /// and refuses to run without it. Each run builds a fresh fixture, as the benchmark's iteration setup does, runs the
    /// rule set through <see cref="Database.ExecuteRuleSet"/> and <see cref="Database.MergeRuleSet"/> (the two halves of
    /// <see cref="Database.RunRuleSet"/>), and checks the result against the reference model. A serial run's counts are
    /// the same on every run; a parallel run's split between inline and spawned batches depends on the schedule. Bytes are
    /// <see cref="GC.GetAllocatedBytesForCurrentThread"/> differences after a warm-up, averaged over many clones.
    /// </remarks>
    internal static class ExecutorCountsReport
    {

        /// <summary>
        /// The command-line option that selects this mode.
        /// </summary>
        public const string Option = "--executor-counts";

        /// <summary>
        /// The runs per size and mode.
        /// </summary>
        const int Runs = 3;

        /// <summary>
        /// The clones each byte figure averages over.
        /// </summary>
        const int Repetitions = 10_000;

        /// <summary>
        /// Runs the workload at every size and mode and prints the counts of each run.
        /// </summary>
        /// <returns>0; 2 when the counters are off.</returns>
        public static int Run()
        {
            if (ExecutorCounters.Enabled == false)
            {
                Console.Error.WriteLine("The executor's counters are off: set EGGNOG_COUNTERS=1 and run again.");
                return 2;
            }

            CloneBytes();
            Console.WriteLine($"## BasicMath executor counts (parallel mode on {RootParallelism.ParallelDegree} threads)");
            foreach (var rows in new[] { 20_000, 200_000 })
            {
                var math = new MathWorkload(rows, RuleSetRunBenchmarks.Seed);
                foreach (var mode in new[] { RunMode.Serial, RunMode.Parallel })
                    for (var run = 0; run < Runs; run++)
                        RunOnce(math, rows, mode, run);
            }

            return 0;
        }

        /// <summary>
        /// Prints the bytes of one clone of a serial fixture's root state, alone and with the mutation buffer of a first
        /// write into <c>num</c>.
        /// </summary>
        static void CloneBytes()
        {
            var graph = new MathEgraph(RunMode.Serial);
            var root = graph.Db.CreateExecutionState();
            var num = graph.Num;
            var clone = Measure(() => GC.KeepAlive(root.Clone(ExecutionStateCloneSite.Other)));
            var withBuffer = Measure(() => GC.KeepAlive(root.Clone(ExecutionStateCloneSite.Other).GetBuffer(num)));
            Console.WriteLine("## Bytes per clone (serial fixture, three tables)");
            Console.WriteLine($"clone of a root state with no buffers: {clone:F0} B");
            Console.WriteLine($"clone and the buffer of its first write into num: {withBuffer:F0} B");
            graph.Db.ExecutorCounts!.Reset();
        }

        /// <summary>
        /// Gets the bytes <paramref name="action"/> allocates on this thread, averaged over <see cref="Repetitions"/> calls
        /// after as many warm-up calls.
        /// </summary>
        static double Measure(Action action)
        {
            for (var i = 0; i < Repetitions; i++)
                action();

            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var i = 0; i < Repetitions; i++)
                action();

            return (GC.GetAllocatedBytesForCurrentThread() - before) / (double)Repetitions;
        }

        /// <summary>
        /// Runs the workload once on a fresh fixture and prints the counts of its execution and its merge.
        /// </summary>
        static void RunOnce(MathWorkload math, int rows, RunMode mode, int run)
        {
            var graph = new MathEgraph(mode);
            using var ruleSet = math.Prepare(graph);
            var counts = graph.Db.ExecutorCounts!;
            counts.Reset();
            var executed = graph.Db.ExecuteRuleSet(ruleSet);
            Console.WriteLine($"{rows} rows, {mode}, run {run}, {math.Adds} matches: execute: {counts}");
            counts.Reset();
            var report = graph.Db.MergeRuleSet(ruleSet, executed);
            Console.WriteLine($"{rows} rows, {mode}, run {run}: merge: {counts}");
            math.Verify(graph, report);
        }

    }

}

using System;
using System.Linq;

using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Diagnosers;
using BenchmarkDotNet.Environments;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Running;
using BenchmarkDotNet.Toolchains.InProcess.Emit;

using Eggnog.Benchmarks.Comparison;
using Eggnog.Benchmarks.Library;
using Eggnog.Benchmarks.Probes;

namespace Eggnog.Relations.Benchmarks
{

    /// <summary>
    /// The entry point: a console app that runs the benchmarks inside its own process, against the Eggnog assembly
    /// (engine included) it was built with. BenchmarkDotNet is used as a library: the in-process toolchain measures each
    /// benchmark here instead of generating, building and launching a project per benchmark, so nothing drives MSBuild.
    /// The configuration adds the memory diagnoser and the item throughput columns (the counterpart of divan's
    /// <c>ItemsCount</c> counter), and keeps the machine's current power plan instead of switching Windows to High
    /// performance for the run.
    /// </summary>
    /// <remarks>
    /// Running in-process means the runtime, garbage collector and JIT settings are this process's own (see the project
    /// file); job options that need a separate process, such as another runtime or other GC modes, are rejected by
    /// BenchmarkDotNet's in-process validator.
    /// </remarks>
    internal static class Program
    {

        /// <summary>
        /// How long one benchmark case may run before the in-process toolchain gives up on it. The largest cases stage
        /// millions of rows per invocation, so this is far above BenchmarkDotNet's default.
        /// </summary>
        static readonly TimeSpan CaseTimeout = TimeSpan.FromMinutes(30);

        /// <summary>
        /// Runs the benchmarks the command line selects, for example <c>--filter *WritesBenchmarks*</c>; with no
        /// arguments, runs every benchmark. A first argument naming a mode runs that mode instead, without
        /// BenchmarkDotNet's runner:
        /// <list type="bullet">
        /// <item><c>--bridge-example math|ac [--threads n]</c> runs an egglog-bridge example program the way its Rust
        /// <c>main</c> does (<see cref="BridgeExampleRunner"/>);</item>
        /// <item><c>--math-microbenchmark</c> runs the library port of egglog's <c>math-microbenchmark.egg</c> the way
        /// the egglog command line runs the file (<see cref="MathMicrobenchmarkRunner"/>);</item>
        /// <item><c>--context-footprint</c> prints the steady-state bytes of the context structures
        /// (<see cref="ContextFootprint"/>);</item>
        /// <item><c>--executor-counts</c> prints the executor's clone, buffer and buffer-map counts for the BasicMath
        /// workload (<see cref="ExecutorCountsReport"/>);</item>
        /// <item><c>--stale-probe &lt;iterations&gt; none|held|caughtup</c> prints what the compaction gate keeps while
        /// the bridge math program runs (<see cref="StaleProbe"/>);</item>
        /// <item><c>--plan-footprint [rules...]</c> prints the bytes a freeze spends per planned rule
        /// (<see cref="PlanFootprint"/>);</item>
        /// <item><c>--allocation-probe</c> counts one case's allocated bytes, by type (<see cref="AllocationProbe"/>);</item>
        /// <item><c>--compare</c> compares builds from their exported reports (<see cref="ComparisonCommand"/>).</item>
        /// </list>
        /// </summary>
        /// <param name="args">BenchmarkDotNet's command-line arguments, or a mode's option and its arguments.</param>
        /// <returns>0 when every selected benchmark ran; 1 when one failed or the configuration was invalid. In a mode,
        /// that mode's exit code.</returns>
        static int Main(string[] args)
        {
            var mode = args.Length > 0 ? args[0] : null;
            switch (mode)
            {
                case BridgeExampleRunner.Option:
                    return BridgeExampleRunner.Run(args.AsSpan(1));
                case ContextFootprint.Option:
                    return ContextFootprint.Run();
                case ExecutorCountsReport.Option:
                    return ExecutorCountsReport.Run();
                case StaleProbe.Option:
                    return StaleProbe.Run(args.AsSpan(1));
                case PlanFootprint.Option:
                    return PlanFootprint.Run(args.AsSpan(1));
                case MathMicrobenchmarkRunner.Option:
                    return MathMicrobenchmarkRunner.Run(args.AsSpan(1));
                case AllocationProbe.Option:
                    return AllocationProbe.Run(args.AsSpan(1), Console.Out, Console.Error);
                case ComparisonCommand.Option:
                    return ComparisonCommand.Run(args.AsSpan(1), Console.Out, Console.Error);
            }

            if (args.Length == 0)
                args = ["--filter", "*"];

            var job = SelectBaseJob(ref args)
                .WithToolchain(new InProcessEmitToolchain(CaseTimeout, logOutput: true))
                .WithPowerPlan(PowerPlan.UserPowerPlan)
                .AsDefault();

            var config = DefaultConfig.Instance
                .AddJob(job)
                .AddDiagnoser(MemoryDiagnoser.Default)
                .AddColumn(ThroughputColumn.Items, ThroughputColumn.ItemsPerSecond);

            var summaries = BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args, config).ToList();
            var failed = summaries.Any(s => s.HasCriticalValidationErrors || s.Reports.Any(r => r.Success == false));
            return failed ? 1 : 0;
        }

        /// <summary>
        /// Takes the base job named by <c>--job</c> (or <c>-j</c>) off the command line. BenchmarkDotNet uses a default job
        /// in the configuration in place of the command line's base job, so the toolchain and power plan settings have to
        /// be applied to the job the command line asks for.
        /// </summary>
        /// <param name="args">The command line; the job option is removed from it.</param>
        /// <returns>The base job: BenchmarkDotNet's default job when the command line names none.</returns>
        /// <exception cref="ArgumentException">The command line names a job BenchmarkDotNet does not define.</exception>
        static Job SelectBaseJob(ref string[] args)
        {
            for (var i = 0; i + 1 < args.Length; i++)
            {
                if (args[i] != "--job" && args[i] != "-j")
                    continue;

                var job = args[i + 1].ToLowerInvariant() switch
                {
                    "default" => Job.Default,
                    "dry" => Job.Dry,
                    "short" => Job.ShortRun,
                    "medium" => Job.MediumRun,
                    "long" => Job.LongRun,
                    "verylong" => Job.VeryLongRun,
                    _ => throw new ArgumentException($"Unknown job '{args[i + 1]}'.", nameof(args)),
                };

                args = args.Take(i).Concat(args.Skip(i + 2)).ToArray();
                return job;
            }

            return Job.Default;
        }

    }

}

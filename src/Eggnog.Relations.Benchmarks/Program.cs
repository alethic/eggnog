using System;
using System.Linq;

using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Diagnosers;
using BenchmarkDotNet.Environments;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Running;
using BenchmarkDotNet.Toolchains.InProcess.Emit;

namespace Eggnog.Relations.Benchmarks
{

    /// <summary>
    /// The entry point: a console app that runs the benchmarks inside its own process, against the engine assembly it was
    /// built with. BenchmarkDotNet is used as a library: the in-process toolchain measures each benchmark here instead of
    /// generating, building and launching a project per benchmark, so nothing drives MSBuild. The configuration adds the
    /// memory diagnoser and the item throughput columns (the counterpart of divan's <c>ItemsCount</c> counter), and keeps
    /// the machine's current power plan instead of switching Windows to High performance for the run.
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
        /// arguments, runs every benchmark.
        /// </summary>
        /// <param name="args">BenchmarkDotNet's command-line arguments.</param>
        /// <returns>0 when every selected benchmark ran; 1 when one failed or the configuration was invalid.</returns>
        static int Main(string[] args)
        {
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

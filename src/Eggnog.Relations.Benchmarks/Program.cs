using System;
using System.Linq;

using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Diagnosers;
using BenchmarkDotNet.Environments;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Running;

namespace Eggnog.Relations.Benchmarks
{

    /// <summary>
    /// The entry point: hands the command line to BenchmarkDotNet's switcher, with a configuration that adds the memory
    /// diagnoser and the item throughput columns (the counterpart of divan's <c>ItemsCount</c> counter), and keeps the
    /// machine's current power plan instead of switching Windows to High performance for the run.
    /// </summary>
    internal static class Program
    {

        /// <summary>
        /// Runs the benchmarks the command line selects, for example <c>--filter *</c>.
        /// </summary>
        /// <param name="args">BenchmarkDotNet's command-line arguments.</param>
        /// <returns>0 when every selected benchmark ran; 1 when one failed or the configuration was invalid.</returns>
        static int Main(string[] args)
        {
            var config = DefaultConfig.Instance
                .AddJob(SelectBaseJob(ref args).WithPowerPlan(PowerPlan.UserPowerPlan).AsDefault())
                .AddDiagnoser(MemoryDiagnoser.Default)
                .AddColumn(ThroughputColumn.Items, ThroughputColumn.ItemsPerSecond);

            var summaries = BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args, config).ToList();
            var failed = summaries.Any(s => s.HasCriticalValidationErrors || s.Reports.Any(r => r.Success == false));
            return failed ? 1 : 0;
        }

        /// <summary>
        /// Takes the base job named by <c>--job</c> (or <c>-j</c>) off the command line. BenchmarkDotNet uses a default job
        /// in the configuration in place of the command line's base job, so the power plan setting has to be applied to
        /// the job the command line asks for.
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

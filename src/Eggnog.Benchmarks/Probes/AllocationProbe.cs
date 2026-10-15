using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime;
using System.Runtime.InteropServices;

namespace Eggnog.Benchmarks.Probes
{

    /// <summary>
    /// The console app's allocation-probe mode, <c>--allocation-probe</c>: runs one benchmark case of this app outside
    /// BenchmarkDotNet (<see cref="BenchmarkInvoker"/>) and reports the bytes each invocation allocates, from
    /// <see cref="GC.GetTotalAllocatedBytes(bool)"/> around the benchmark method, and where they go by type, from the
    /// runtime's allocation ticks over the same windows (<see cref="AllocationTickListener"/>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Usage: <c>--allocation-probe &lt;Class&gt;[.&lt;Method&gt;] [&lt;Parameter&gt;=&lt;value&gt;]... [--warmup n]
    /// [--repetitions n] [--types n]</c>; by default one warm-up invocation, five measured and the fifteen largest types.
    /// A parameter not given takes its first value.
    /// </para>
    /// <para>
    /// The listener's own event dispatch allocates on its thread while the case runs (the runtime's
    /// <c>MoreEventInfo</c> and the event arguments: about 1.4 KB per tick, 0.4% of the bytes of <c>BasicMath</c> at
    /// 200,000 rows), and those bytes are in both figures. <c>--types 0</c> runs without the listener, for the bytes
    /// alone.
    /// </para>
    /// <para>
    /// The probe also reports the process's CPU time per measured invocation (<see cref="Process.TotalProcessorTime"/>
    /// around the method, every thread's, the listener's included): for a serial case, a signal that a busy machine
    /// moves less than it moves wall-clock time, though cache and frequency effects still reach it. It is an early
    /// signal only, never a gate.
    /// </para>
    /// <para>
    /// Byte counts do not depend on the machine's load, so this needs no idle machine. They do depend on the runtime
    /// and the collector: the process's own (run the net8.0 or net10.0 build; set <c>DOTNET_gcServer=0</c> for the
    /// workstation collector). The counts are of every thread, so the case's parallel workers are included, and so is
    /// anything else the process allocates meanwhile, which in this mode is nothing of note.
    /// </para>
    /// </remarks>
    internal static class AllocationProbe
    {

        /// <summary>
        /// The command-line option that selects this mode.
        /// </summary>
        public const string Option = "--allocation-probe";

        /// <summary>
        /// The usage line printed for a bad command line.
        /// </summary>
        const string Usage = "usage: Eggnog.Benchmarks --allocation-probe <Class>[.<Method>] [<Parameter>=<value>]... [--warmup <n>] [--repetitions <n>] [--types <n>]";

        /// <summary>
        /// Runs the probe with the arguments after <see cref="Option"/>.
        /// </summary>
        /// <param name="args">The arguments after <see cref="Option"/>.</param>
        /// <param name="output">Where the report is written.</param>
        /// <param name="error">Where errors are written.</param>
        /// <returns>0 when the case ran; 1 when it threw; 2 for a bad command line.</returns>
        public static int Run(ReadOnlySpan<string> args, TextWriter output, TextWriter error)
        {
            ArgumentNullException.ThrowIfNull(output);
            ArgumentNullException.ThrowIfNull(error);

            string? name = null;
            var values = new Dictionary<string, string>(StringComparer.Ordinal);
            int warmup = 1, repetitions = 5, types = 15;
            BenchmarkInvoker invoker;
            try
            {
                for (var i = 0; i < args.Length; i++)
                {
                    var arg = args[i];
                    if (arg is "--warmup" or "--repetitions" or "--types")
                    {
                        if (i + 1 >= args.Length || int.TryParse(args[i + 1], NumberStyles.None, CultureInfo.InvariantCulture, out var n) == false)
                            throw new ArgumentException($"{arg} takes a count.");

                        i++;
                        if (arg == "--warmup")
                            warmup = n;
                        else if (arg == "--repetitions")
                            repetitions = n;
                        else
                            types = n;
                    }
                    else if (arg.IndexOf('=', StringComparison.Ordinal) is > 0 and var equals)
                    {
                        values[arg.Substring(0, equals)] = arg.Substring(equals + 1);
                    }
                    else if (name == null && arg.StartsWith("--", StringComparison.Ordinal) == false)
                    {
                        name = arg;
                    }
                    else
                    {
                        throw new ArgumentException($"Unexpected argument '{arg}'.");
                    }
                }

                if (name == null || repetitions < 1)
                    throw new ArgumentException("A benchmark and at least one repetition are required.");

                invoker = BenchmarkInvoker.Create(Assembly.GetExecutingAssembly(), name, values);
            }
            catch (ArgumentException e)
            {
                error.WriteLine(e.Message);
                error.WriteLine(Usage);
                return 2;
            }

            output.WriteLine($"Case: {invoker.Name}({string.Join(", ", invoker.Parameters.Select(p => $"{p.Key}={Format(p.Value)}"))})");
            output.WriteLine($"Runtime: {RuntimeInformation.FrameworkDescription}, {(GCSettings.IsServerGC ? "server" : "workstation")} GC "
                + $"({GCSettings.LatencyMode}), {Environment.ProcessorCount} logical processors");
            output.WriteLine($"Invocations: {warmup} warm-up, {repetitions} measured");

            using var listener = types > 0 ? new AllocationTickListener() : null;
            using var process = Process.GetCurrentProcess();
            var bytes = new List<long>(repetitions);
            var cpu = new List<TimeSpan>(repetitions);
            try
            {
                invoker.GlobalSetup();
                for (var i = 0; i < warmup + repetitions; i++)
                {
                    invoker.IterationSetup();
                    var measured = i >= warmup;
                    if (measured)
                        listener?.Open();

                    var cpuBefore = process.TotalProcessorTime;
                    var before = GC.GetTotalAllocatedBytes(true);
                    var result = invoker.Invoke();
                    var after = GC.GetTotalAllocatedBytes(true);
                    var cpuAfter = process.TotalProcessorTime;
                    if (measured)
                    {
                        listener?.Close();
                        bytes.Add(after - before);
                        cpu.Add(cpuAfter - cpuBefore);
                    }

                    GC.KeepAlive(result);
                    invoker.IterationCleanup();
                }

                invoker.GlobalCleanup();
            }
            catch (Exception e)
            {
                error.WriteLine($"The case threw: {e}");
                return 1;
            }

            var mean = bytes.Average();
            output.WriteLine($"Bytes per invocation: {mean.ToString("N0", CultureInfo.InvariantCulture)} "
                + $"(min {bytes.Min().ToString("N0", CultureInfo.InvariantCulture)}, max {bytes.Max().ToString("N0", CultureInfo.InvariantCulture)})");
            if (invoker.OperationsPerInvoke > 1)
                output.WriteLine($"Bytes per operation: {(mean / invoker.OperationsPerInvoke).ToString("N0", CultureInfo.InvariantCulture)} "
                    + $"({invoker.OperationsPerInvoke} operations per invocation)");

            output.WriteLine($"Process CPU time per invocation: {Milliseconds(TimeSpan.FromTicks((long)cpu.Average(t => t.Ticks)))} "
                + $"(min {Milliseconds(cpu.Min())}, max {Milliseconds(cpu.Max())}); an early signal, not a timing");

            if (listener == null)
                return 0;

            var fenced = listener.Fence(TimeSpan.FromSeconds(10));
            var byType = listener.BytesByType();
            var sampled = byType.Sum(p => p.Value);
            output.WriteLine($"Sampled bytes by type: {listener.Ticks.ToString("N0", CultureInfo.InvariantCulture)} allocation ticks, "
                + $"{sampled.ToString("N0", CultureInfo.InvariantCulture)} bytes over the measured invocations"
                + (fenced ? "" : " (the last ticks may not have arrived)"));
            if (sampled == 0)
                return 0;

            var width = Math.Max(4, byType.Take(types).Select(p => p.Key.Length).DefaultIfEmpty(0).Max());
            foreach (var (type, total) in byType.Take(types))
                output.WriteLine($"  {type.PadRight(width)}  {total.ToString("N0", CultureInfo.InvariantCulture),18}  {Share(total, sampled),6}");

            var rest = byType.Skip(types).Sum(p => p.Value);
            if (rest > 0)
                output.WriteLine($"  {$"({byType.Count - types} other types)".PadRight(width)}  {rest.ToString("N0", CultureInfo.InvariantCulture),18}  {Share(rest, sampled),6}");

            return 0;
        }

        /// <summary>
        /// Formats a time in milliseconds with three decimals.
        /// </summary>
        /// <param name="time">The time.</param>
        /// <returns>The text, for example <c>61.250 ms</c>.</returns>
        static string Milliseconds(TimeSpan time) => time.TotalMilliseconds.ToString("0.000", CultureInfo.InvariantCulture) + " ms";

        /// <summary>
        /// Formats a share as a percentage with one decimal.
        /// </summary>
        /// <param name="part">The part.</param>
        /// <param name="whole">The whole, above zero.</param>
        /// <returns>The text, for example <c>48.2%</c>.</returns>
        static string Share(long part, long whole) => (100.0 * part / whole).ToString("0.0", CultureInfo.InvariantCulture) + "%";

        /// <summary>
        /// Formats a parameter value in the invariant culture.
        /// </summary>
        /// <param name="value">The value.</param>
        /// <returns>The text.</returns>
        static string Format(object? value) => value is IFormattable f ? f.ToString(null, CultureInfo.InvariantCulture) : value?.ToString() ?? "null";

    }

}

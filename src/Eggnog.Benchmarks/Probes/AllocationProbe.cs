using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace Eggnog.Benchmarks.Probes
{

    /// <summary>
    /// The console app's allocation-probe mode, <c>--allocation-probe</c>: runs one benchmark case of this app outside
    /// BenchmarkDotNet (<see cref="BenchmarkInvoker"/>) and reports the bytes each invocation allocates, counted around
    /// the benchmark method, and where they go by type, from the runtime's allocation ticks over the same windows
    /// (<see cref="AllocationTickListener"/>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Usage: <c>--allocation-probe &lt;Class&gt;[.&lt;Method&gt;] [&lt;Parameter&gt;=&lt;value&gt;]... [--warmup n]
    /// [--repetitions n] [--types n] [--per-thread]</c>; by default one warm-up invocation, five measured and the fifteen
    /// largest types. A parameter not given takes its first value.
    /// </para>
    /// <para>
    /// By default the bytes are counted process-wide, with <see cref="GC.GetTotalAllocatedBytes(bool)"/>: every thread's,
    /// so the case's parallel workers are included, and so is anything else the process allocates meanwhile, which in
    /// this mode is nothing of note. Under the server collector with dynamic adaptation (the default since .NET 9) a
    /// difference of two precise readings can be off by gigabytes or negative, so an invocation whose precise and
    /// imprecise readings disagree by more than <see cref="Tolerance"/> is dropped and counted as dropped; that check
    /// catches gross errors only. Under the workstation collector, and under the server collector with
    /// <c>DOTNET_GCDynamicAdaptationMode=0</c>, the counter is reliable.
    /// </para>
    /// <para>
    /// <c>--per-thread</c> counts the bytes per thread instead (<see cref="AllocationCountingScheduler"/>): the method
    /// runs as a task on a scheduler of dedicated threads, on which a benchmark class that implements
    /// <see cref="IScheduledBenchmark"/> runs its parallel loops too, and each thread's bytes are counted exactly under
    /// either collector. Before the case the probe checks that count on a known allocation
    /// spread over the threads, and stops if it misses it. A case that queues work to the thread pool (a parallel rule
    /// run) cannot be counted so, and the probe stops with an error. Each invocation also reads the process-wide counter,
    /// and the probe reports how many of those readings were off by more than <see cref="Tolerance"/>.
    /// </para>
    /// <para>
    /// The listener's own event dispatch allocates on its thread while the case runs (the runtime's
    /// <c>MoreEventInfo</c> and the event arguments: about 1.4 KB per tick, 0.4% of the bytes of <c>BasicMath</c> at
    /// 200,000 rows), and the process-wide count includes those bytes. <c>--types 0</c> runs without the listener, for
    /// the bytes alone.
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
    /// workstation collector).
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
        const string Usage = "usage: Eggnog.Benchmarks --allocation-probe <Class>[.<Method>] [<Parameter>=<value>]... [--warmup <n>] [--repetitions <n>] [--types <n>] [--per-thread]";

        /// <summary>
        /// The difference between two counts of one invocation above which they disagree: 1 MiB, far above what the
        /// probe's own thread allocates around an invocation and far below the process-wide counter's errors.
        /// </summary>
        const long Tolerance = 1 << 20;

        /// <summary>
        /// The bytes each iteration of <see cref="CheckCounter"/> allocates: 4 MiB, far above the
        /// <see cref="Tolerance"/> a missed iteration must exceed.
        /// </summary>
        const int CheckArrayBytes = 4 << 20;

        /// <summary>
        /// The fewest dedicated threads of the per-thread count: the largest thread count of the app's parallel
        /// benchmarks.
        /// </summary>
        const int MinimumCountingThreads = 8;

        /// <summary>
        /// Runs the probe with the arguments after <see cref="Option"/>.
        /// </summary>
        /// <param name="args">The arguments after <see cref="Option"/>.</param>
        /// <param name="output">Where the report is written.</param>
        /// <param name="error">Where errors are written.</param>
        /// <returns>0 when the case ran and was counted; 1 when it threw, when the per-thread count failed its check or
        /// missed thread-pool work, or when no invocation was counted reliably; 2 for a bad command line.</returns>
        public static int Run(ReadOnlySpan<string> args, TextWriter output, TextWriter error)
        {
            ArgumentNullException.ThrowIfNull(output);
            ArgumentNullException.ThrowIfNull(error);

            string? name = null;
            var values = new Dictionary<string, string>(StringComparer.Ordinal);
            int warmup = 1, repetitions = 5, types = 15;
            var perThread = false;
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
                    else if (arg == "--per-thread")
                    {
                        perThread = true;
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

            var adaptation = Environment.GetEnvironmentVariable("DOTNET_GCDynamicAdaptationMode")
                ?? AppContext.GetData("System.GC.DynamicAdaptationMode")?.ToString()
                ?? "runtime default";
            output.WriteLine($"Case: {invoker.Name}({string.Join(", ", invoker.Parameters.Select(p => $"{p.Key}={Format(p.Value)}"))})");
            output.WriteLine($"Runtime: {RuntimeInformation.FrameworkDescription}, {(GCSettings.IsServerGC ? "server" : "workstation")} GC "
                + $"({GCSettings.LatencyMode}, dynamic adaptation: {adaptation}), {Environment.ProcessorCount} logical processors");
            output.WriteLine($"Invocations: {warmup} warm-up, {repetitions} measured");

            using var counter = perThread ? new AllocationCountingScheduler(Math.Max(MinimumCountingThreads, Environment.ProcessorCount)) : null;
            if (counter != null && CheckCounter(counter, output) == false)
                return 1;

            if (counter != null && invoker.Instance is IScheduledBenchmark scheduled)
                scheduled.Scheduler = counter;

            using var listener = types > 0 ? new AllocationTickListener() : null;
            using var process = Process.GetCurrentProcess();
            var bytes = new List<long>(repetitions);
            var cpu = new List<TimeSpan>(repetitions);
            var offCount = 0;
            object? result = null;
            Action body = () => result = invoker.Invoke();
            try
            {
                invoker.GlobalSetup();
                for (var i = 0; i < warmup + repetitions; i++)
                {
                    invoker.IterationSetup();
                    var measured = i >= warmup;
                    if (measured)
                        listener?.Open();

                    long threadBytes = 0, poolItems = 0;
                    var cpuBefore = process.TotalProcessorTime;
                    var before = GC.GetTotalAllocatedBytes(true);
                    var beforeImprecise = GC.GetTotalAllocatedBytes(false);
                    if (counter == null)
                        result = invoker.Invoke();
                    else
                        (threadBytes, poolItems) = counter.Count(body);

                    var afterImprecise = GC.GetTotalAllocatedBytes(false);
                    var after = GC.GetTotalAllocatedBytes(true);
                    var cpuAfter = process.TotalProcessorTime;
                    if (poolItems != 0)
                    {
                        error.WriteLine($"{poolItems} thread-pool work items ran during an invocation, so the per-thread count misses their bytes; "
                            + "count this case without --per-thread.");
                        return 1;
                    }

                    if (measured)
                    {
                        listener?.Close();
                        var processWide = after - before;
                        if (counter != null)
                        {
                            bytes.Add(threadBytes);
                            if (Math.Abs(processWide - threadBytes) > Tolerance)
                                offCount++;
                        }
                        else if (processWide < 0 || Math.Abs(processWide - (afterImprecise - beforeImprecise)) > Tolerance)
                        {
                            offCount++;
                        }
                        else
                        {
                            bytes.Add(processWide);
                        }

                        cpu.Add(cpuAfter - cpuBefore);
                    }

                    GC.KeepAlive(result);
                    result = null;
                    invoker.IterationCleanup();
                }

                invoker.GlobalCleanup();
            }
            catch (Exception e)
            {
                error.WriteLine($"The case threw: {e}");
                return 1;
            }

            output.WriteLine(counter != null
                ? $"Counted per thread on {counter.MaximumConcurrencyLevel} dedicated threads; the process-wide counter was off by more than 1 MiB in {offCount} of {repetitions} invocations"
                : $"Counted process-wide; {offCount} of {repetitions} invocations dropped, their precise and imprecise readings more than 1 MiB apart");
            if (bytes.Count == 0)
            {
                error.WriteLine("No invocation was counted reliably; count this case with --per-thread, or with DOTNET_GCDynamicAdaptationMode=0.");
                return 1;
            }

            var mean = bytes.Average();
            output.WriteLine($"Bytes per invocation: {mean.ToString("N0", CultureInfo.InvariantCulture)} "
                + $"(median {Median(bytes).ToString("N0", CultureInfo.InvariantCulture)}, "
                + $"min {bytes.Min().ToString("N0", CultureInfo.InvariantCulture)}, max {bytes.Max().ToString("N0", CultureInfo.InvariantCulture)})");
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
        /// Checks <paramref name="counter"/> on a known allocation: a parallel loop over its threads, each iteration of
        /// which allocates <see cref="CheckArrayBytes"/> bytes. The count must cover every iteration's array, and exceed
        /// them by less than <see cref="Tolerance"/> (the loop's own bookkeeping). Writes the result.
        /// </summary>
        /// <param name="counter">The counting scheduler.</param>
        /// <param name="output">Where the result is written.</param>
        /// <returns>Whether the count passed.</returns>
        static bool CheckCounter(AllocationCountingScheduler counter, TextWriter output)
        {
            var iterations = counter.MaximumConcurrencyLevel;
            var arrays = new byte[iterations][];
            var options = new ParallelOptions { MaxDegreeOfParallelism = iterations, TaskScheduler = counter };
            var bytes = 0L;
            for (var run = 0; run < 2; run++)
                (bytes, _) = counter.Count(() => Parallel.For(0, iterations, options, i => arrays[i] = new byte[CheckArrayBytes]));

            GC.KeepAlive(arrays);
            var expected = (long)iterations * CheckArrayBytes;
            var passed = bytes >= expected && bytes - expected < Tolerance;
            output.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"Counter check: {bytes:N0} bytes for {iterations} arrays of {CheckArrayBytes:N0} bytes; {(passed ? "passed" : "FAILED")}"));
            return passed;
        }

        /// <summary>
        /// Gets the median of some counts.
        /// </summary>
        /// <param name="counts">The counts; at least one.</param>
        /// <returns>The median: the middle count, or the mean of the two middle counts.</returns>
        static long Median(List<long> counts)
        {
            var sorted = counts.Order().ToList();
            var middle = sorted.Count / 2;
            return sorted.Count % 2 == 1 ? sorted[middle] : (sorted[middle - 1] + sorted[middle]) / 2;
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

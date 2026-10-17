using System;
using System.Globalization;
using System.Runtime;
using System.Runtime.CompilerServices;

namespace Eggnog.Relations.Benchmarks
{

    /// <summary>
    /// The console app's stale-row probe, <c>--stale-probe &lt;iterations&gt; none|held|caughtup</c>: runs
    /// <see cref="BridgeMathProgram"/> at its root, one <see cref="EGraph.RunRules"/> call per iteration, with no child,
    /// with a child of the root held unused, or with one caught up after every iteration
    /// (<see cref="StaleProbeChild"/>), and prints what the compaction gate keeps: the tombstones of the 13 function
    /// tables, the compactions the gate blocked, the tombstones at or above the child's cursor and above the compaction
    /// trigger's bound, and at the end the bytes of the tables' storage and of the managed heap, attributed. Counts and
    /// bytes only: no time is measured, so the figures need no idle machine.
    /// </summary>
    /// <remarks>
    /// <para>
    /// After each iteration (before the caught-up child catches up), one line sums over the function tables: live rows
    /// (<see cref="SortedWritesTable.Count"/>), physical rows, tombstones (<see cref="SortedWritesTable.StaleCount"/>),
    /// compactions blocked so far (<see cref="SortedWritesTable.CompactionsBlocked"/>), the tombstones at or above the
    /// child's cursor, found by scanning <see cref="RowBuffer.IsStale"/> from each table's physical row count when the
    /// child was created or last caught up, and the tombstones above the trigger's bound, <c>max(0, stale − max(16,
    /// physical / 2))</c> per table: those egglog's trigger would remove at the next merge.
    /// </para>
    /// <para>
    /// At the end, with the child still live: the row buffers' capacity and bytes in use, the compaction scratch buffers
    /// (a parallel compaction keeps the storage the rows had before it), the key indexes, and the managed heap after two
    /// forced, blocking, compacting collections (large objects included). The heap is attributed by taking it three
    /// times: before the program is built, with the graph live, and after the graph is released. The difference between
    /// the last two is the graph's own memory, of which the table storage above is a part; the difference between the
    /// first and the last is what the process keeps once the graph is gone, chiefly arrays that compaction, indexes and
    /// row buffers returned to <see cref="System.Buffers.ArrayPool{T}.Shared"/>, which the heap counts.
    /// </para>
    /// <para>
    /// Each iteration checks the counts it prints: neither tombstone column exceeds the tombstones, nothing is blocked
    /// without a child, and no table compacts while the child is live, which would invalidate the cursors. With all 12
    /// iterations, the run's table sizes are also checked against egglog's (<see cref="BridgeMathProgram.Verify"/>), so
    /// a child that changed the root's result fails the probe.
    /// </para>
    /// </remarks>
    internal static class StaleProbe
    {

        /// <summary>
        /// The command-line option that selects this mode; the iteration count and the child follow it.
        /// </summary>
        public const string Option = "--stale-probe";

        const double MiB = 1024 * 1024;

        /// <summary>
        /// Runs the probe the arguments after <see cref="Option"/> describe.
        /// </summary>
        /// <param name="args">The number of iterations (1 to <see cref="BridgeMathProgram.N"/>), then <c>none</c>,
        /// <c>held</c> or <c>caughtup</c>.</param>
        /// <returns>0 when the probe ran, its counts checked out and, with every iteration, the result matched
        /// egglog's; 1 when a check failed; 2 for a bad command line.</returns>
        public static int Run(ReadOnlySpan<string> args)
        {
            if (args.Length != 2 || int.TryParse(args[0], NumberStyles.None, CultureInfo.InvariantCulture, out var iterations) == false || iterations < 1 || iterations > BridgeMathProgram.N || TryParseChild(args[1], out var child) == false)
            {
                Console.Error.WriteLine($"usage: Eggnog.Benchmarks {Option} <iterations 1-{BridgeMathProgram.N}> none|held|caughtup");
                return 2;
            }

            Console.WriteLine($"## Stale rows: math, {iterations} iterations, child: {args[1]}, server GC: {GCSettings.IsServerGC}");
            var before = CollectedHeap();
            var (heap, storage, error) = RunProgram(iterations, child);
            var after = CollectedHeap();

            Console.WriteLine();
            Console.WriteLine("| Managed heap after forced collections | MiB |");
            Console.WriteLine("|---|---:|");
            Console.WriteLine($"| before the program | {Format(before / MiB)} |");
            Console.WriteLine($"| with the graph live | {Format(heap / MiB)} |");
            Console.WriteLine($"| after the graph is released | {Format(after / MiB)} |");
            Console.WriteLine($"| the graph's own (live - released) | {Format((heap - after) / MiB)} |");
            Console.WriteLine($"| of which table storage (rows, scratch, key indexes) | {Format(storage / MiB)} |");
            Console.WriteLine($"| of which everything else (indexes, union-find, plans, base values) | {Format((heap - after - storage) / MiB)} |");
            Console.WriteLine($"| kept after release (pooled arrays, caches) | {Format((after - before) / MiB)} |");

            if (error != null)
            {
                Console.Error.WriteLine(error);
                return 1;
            }

            return 0;
        }

        /// <summary>
        /// Builds the program, runs it with <paramref name="child"/>, prints the per-iteration and end-of-run lines, and
        /// takes the heap while the graph is live. Not inlined, so the graph is unreachable once it returns.
        /// </summary>
        /// <returns>The heap with the graph live, the bytes of the tables' storage, and the failed check's message
        /// (<c>null</c> when it passed or did not run).</returns>
        [MethodImpl(MethodImplOptions.NoInlining)]
        static (long Heap, long Storage, string? Error) RunProgram(int iterations, StaleProbeChild child)
        {
            var program = new BridgeMathProgram();
            var egraph = program.EGraph;
            var functions = program.Functions;
            var tables = new SortedWritesTable[functions.Length];
            for (var i = 0; i < functions.Length; i++)
                tables[i] = egraph.Store.Schema[egraph.GetTable(functions[i])]!.Root;

            var context = child == StaleProbeChild.None ? null : egraph.Root.CreateChild();
            var cursors = new int[tables.Length];
            MoveCursors(tables, cursors);
            var generations = new int[tables.Length];
            for (var t = 0; t < tables.Length; t++)
                generations[t] = tables[t].Version.Major;

            string? error = null;

            Console.WriteLine();
            Console.WriteLine("| Iteration | Live rows | Physical rows | Tombstones | Blocked so far | At or above the cursor | Above the trigger's bound |");
            Console.WriteLine("|---:|---:|---:|---:|---:|---:|---:|");
            var ran = 0;
            var saturated = false;
            for (var i = 0; i < iterations; i++)
            {
                var changed = program.RunIteration();
                ran++;
                long live = 0, physical = 0, stale = 0, blocked = 0, aboveCursor = 0, aboveBound = 0;
                for (var t = 0; t < tables.Length; t++)
                {
                    var table = tables[t];
                    var rows = table.Rows;
                    live += table.Count;
                    physical += rows.Count;
                    stale += table.StaleCount;
                    blocked += table.CompactionsBlocked;
                    aboveBound += Math.Max(0, table.StaleCount - Math.Max(16, rows.Count / 2));
                    if (context != null)
                    {
                        for (var row = Math.Min(cursors[t], rows.Count); row < rows.Count; row++)
                            if (rows.IsStale(row))
                                aboveCursor++;

                        if (table.Version.Major != generations[t])
                            error ??= $"Iteration {i}: table {t} compacted while the child was live.";
                    }
                }

                var cursorCell = context != null ? Format(aboveCursor) : "-";
                Console.WriteLine($"| {i} | {Format(live)} | {Format(physical)} | {Format(stale)} | {Format(blocked)} | {cursorCell} | {Format(aboveBound)} |");
                if (aboveCursor > stale || aboveBound > stale)
                    error ??= $"Iteration {i}: more tombstones above the cursor or the trigger's bound than in all.";
                if (context == null && blocked != 0)
                    error ??= $"Iteration {i}: {blocked} compactions blocked without a child.";

                if (child == StaleProbeChild.CaughtUp)
                {
                    context!.EnsureClean();
                    MoveCursors(tables, cursors);
                }

                if (changed == false)
                {
                    saturated = true;
                    break;
                }
            }

            long capacity = 0, used = 0, scratch = 0, keys = 0, liveAtEnd = 0, physicalAtEnd = 0;
            foreach (var table in tables)
            {
                capacity += table.RowStorageBytes;
                used += (long)table.Rows.Count * table.Arity * sizeof(uint);
                scratch += table.ScratchStorageBytes;
                keys += table.KeyIndexBytes;
                liveAtEnd += table.Count;
                physicalAtEnd += table.Rows.Count;
            }

            Console.WriteLine();
            Console.WriteLine("| At the end | Value |");
            Console.WriteLine("|---|---:|");
            Console.WriteLine($"| physical rows / live rows | {(liveAtEnd == 0 ? "-" : (physicalAtEnd / (double)liveAtEnd).ToString("F2", CultureInfo.InvariantCulture))} |");
            Console.WriteLine($"| row-buffer capacity (MiB) | {Format(capacity / MiB)} |");
            Console.WriteLine($"| row-buffer bytes in use (MiB) | {Format(used / MiB)} |");
            Console.WriteLine($"| compaction scratch buffers (MiB) | {Format(scratch / MiB)} |");
            Console.WriteLine($"| key indexes (MiB) | {Format(keys / MiB)} |");

            if (error == null && iterations == BridgeMathProgram.N)
            {
                try
                {
                    program.Verify(ran, saturated);
                }
                catch (InvalidOperationException e)
                {
                    error = e.Message;
                }
            }

            var heap = CollectedHeap();
            GC.KeepAlive(program);
            GC.KeepAlive(context);
            context?.Dispose();
            return (heap, capacity + scratch + keys, error);
        }

        /// <summary>
        /// Sets each table's cursor to its physical row count: where a child created or caught up now has its cursor.
        /// </summary>
        static void MoveCursors(SortedWritesTable[] tables, int[] cursors)
        {
            for (var t = 0; t < tables.Length; t++)
                cursors[t] = tables[t].Rows.Count;
        }

        /// <summary>
        /// Collects twice, forced, blocking and compacting (the large object heap included), and gets the managed heap's
        /// size.
        /// </summary>
        static long CollectedHeap()
        {
            for (var i = 0; i < 2; i++)
            {
                GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
                GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
                GC.WaitForPendingFinalizers();
            }

            return GC.GetTotalMemory(false);
        }

        static bool TryParseChild(string text, out StaleProbeChild child)
        {
            switch (text)
            {
                case "none":
                    child = StaleProbeChild.None;
                    return true;
                case "held":
                    child = StaleProbeChild.Held;
                    return true;
                case "caughtup":
                    child = StaleProbeChild.CaughtUp;
                    return true;
                default:
                    child = default;
                    return false;
            }
        }

        static string Format(long value) => value.ToString("N0", CultureInfo.InvariantCulture);

        static string Format(double value) => value.ToString("N1", CultureInfo.InvariantCulture);

    }

}

using System;
using System.Globalization;

namespace Eggnog.Relations.Benchmarks
{

    /// <summary>
    /// The console app's footprint mode, <c>--context-footprint</c> (colored-contexts spec §14.2, §17 M10.4, L6, L7, G3):
    /// prints the bytes the structures of colored contexts allocate and the counters of the case-split workloads,
    /// measured in this process. Allocation counts and counters do not depend on the machine's load, so these figures need
    /// no idle machine; times are not measured.
    /// </summary>
    /// <remarks>
    /// Bytes are <see cref="GC.GetAllocatedBytesForCurrentThread"/> differences after a warm-up, averaged over many
    /// repetitions, so they are the memory diagnoser's steady state without the cold-start allocations (JIT, static
    /// initialization) that a dry run's single invocation includes. Every workload runs serially on the calling thread,
    /// and each repetition creates and disposes what it measures, as the matching benchmark's invocation does.
    /// </remarks>
    internal static class ContextFootprint
    {

        /// <summary>
        /// The command-line option that selects this mode.
        /// </summary>
        public const string Option = "--context-footprint";

        /// <summary>
        /// Runs every footprint workload and prints one line per figure.
        /// </summary>
        /// <returns>0.</returns>
        public static int Run()
        {
            Children();
            CaseSplits();
            CaseParts();
            LateParentMints();
            CopyVolumes();
            return 0;
        }

        /// <summary>
        /// Bytes of a bare child, an empty layer, their parts, a one-union child and the full per-child workload.
        /// </summary>
        static void Children()
        {
            Console.WriteLine("## Children (bytes per operation)");
            var root = new RootWorkload(20_000);
            var bare = Measure(() => root.Store.Root.CreateChild().Dispose());
            Print("storage child, no layer (ManyChildrenBenchmarks.ChildOnly)", bare);
            var layer = Measure(() =>
            {
                var child = root.Store.Root.CreateChild();
                child.Tables!.GetOrCreateLayer(root.F);
                child.Dispose();
            });
            Print("storage child with one empty layer (ManyChildrenBenchmarks.ChildWithOneEmptyLayer)", layer);
            Print("  one empty layer (the difference)", layer - bare);
            Print("storage child, one union and a rebuild (3 rows copied)", Measure(() =>
            {
                var child = root.Store.Root.CreateChild();
                child.Union(root.Ids[0], root.Ids[1]);
                child.Rebuild();
                child.Dispose();
            }));

            var schema = root.Store.Schema[root.F]!;
            Print("  part: PendingState, one shard, eager (root tables)", Measure(() => GC.KeepAlive(new PendingState(new ShardData(1)))));
            Print("  part: PendingState, one shard, lazy (child tables)", Measure(() => GC.KeepAlive(new PendingState(new ShardData(1), lazyQueues: true))));
            Print("  part: TableInfo (two IndexCatalogs)", Measure(() => GC.KeepAlive(new TableInfo("f", schema.Root, ParallelHeuristics.Serial))));
            Print("  part: NotificationList, eager (root catalogs)", Measure(() => GC.KeepAlive(new NotificationList<TableId>())));
            Print("  part: NotificationList, lazy (child catalogs)", Measure(() => GC.KeepAlive(new NotificationList<TableId>(lazy: true))));
            Print("  part: UnionFindOverlay", Measure(() => GC.KeepAlive(new UnionFindOverlay())));
            Print("  part: DisplacedJournal", Measure(() => GC.KeepAlive(new DisplacedJournal())));
            Print("  part: IdBlockAllocator", Measure(() => GC.KeepAlive(new IdBlockAllocator(root.Store.Counters, root.Store.IdCounter))));

            var graph = new CaseGraph(20_000);
            graph.Root.Freeze();
            Print("bridge child, no layer (EGraphContext.CreateChild, Dispose)", Measure(() => graph.Root.CreateChild().Dispose()));
            foreach (var rows in new[] { 20_000, 200_000 })
            {
                var full = new CaseGraph(rows, runCopy: true);
                full.Root.Freeze();
                Print($"full per-child workload, frozen root of {rows:N0} (100 unions, flush, one rule run)", Measure(() =>
                {
                    var child = full.Root.CreateChild();
                    for (var i = 0; i < PerChildCostBenchmarks.Unions; i++)
                        child.StageUnion(full.Ids[2 * i], full.Ids[2 * i + 1]);

                    child.FlushUpdates();
                    child.RunRules(full.Copy);
                    child.Dispose();
                }, warmup: 20, repetitions: 200));
            }
        }

        /// <summary>
        /// L6, B5: bytes per case of a split, with contexts and with clones.
        /// </summary>
        static void CaseSplits()
        {
            Console.WriteLine();
            Console.WriteLine("## Case splits (L6): bytes per case");
            foreach (var rows in new[] { 20_000, 200_000 })
            {
                var contexts = new CaseGraph(rows);
                contexts.Root.Freeze();
                var clones = new CaseGraph(rows);
                foreach (var cases in new[] { 2, 8 })
                {
                    var scope = Measure(() => contexts.Root.CreateChild().Dispose());
                    var plain = Measure(() => CaseSplitMemoryBenchmarks.RunContexts(contexts, cases, lift: false), warmup: 20, repetitions: 200);
                    var lifted = Measure(() => CaseSplitMemoryBenchmarks.RunContexts(contexts, cases, lift: true), warmup: 20, repetitions: 200);
                    var cloned = Measure(() => CaseSplitMemoryBenchmarks.RunClones(clones, cases), warmup: 1, repetitions: 3);
                    Print($"root {rows:N0}, {cases} cases: contexts, per case (scope excluded)", (plain - scope) / cases);
                    Print($"root {rows:N0}, {cases} cases: contexts with lift, per case (scope excluded)", (lifted - scope) / cases);
                    Print($"root {rows:N0}, {cases} cases: Database.Clone, per case", cloned / cases);
                }
            }
        }

        /// <summary>
        /// L6, B5: the bytes of one case, structure by structure. Each line adds one step to the case's work, under a fresh
        /// writable scope over a frozen root of 20,000 rows, and prints what the step adds (the scope's own cost is
        /// subtracted): the case itself, a union and its rebuild, an asserted fact, a rule run, and a lift.
        /// </summary>
        static void CaseParts()
        {
            Console.WriteLine();
            Console.WriteLine("## One case, structure by structure (L6, B5): bytes each step adds");
            var graph = new CaseGraph(20_000, runCopy: true);
            graph.Root.Freeze();
            var scope = Measure(() => graph.Root.CreateChild().Dispose());
            var previous = 0.0;
            for (var steps = 1; steps <= 5; steps++)
            {
                var upTo = steps;
                var bytes = Measure(() => RunCase(graph, upTo), warmup: 20, repetitions: 400) - scope;
                Print(CasePartNames[steps - 1], bytes - previous);
                previous = bytes;
            }

            Print("one case with all of the above (scope excluded)", previous);
        }

        /// <summary>
        /// The names of <see cref="CaseParts"/>' steps, in order.
        /// </summary>
        static readonly string[] CasePartNames =
        [
            "  part: the case (CaseSet, child context, its database and catalogs)",
            "  part: one union and its rebuild (overlay, union-find layer, catch-up copies, hidden sets, cursors)",
            "  part: one asserted fact (the fact table's own layer)",
            "  part: one rule run (rule progress and watermarks, the run's indexes, caches)",
            "  part: one lift over the case (member map, candidate arenas, lift writes, the scope's flush)",
        ];

        /// <summary>
        /// Opens one case under a fresh scope of <paramref name="graph"/>'s frozen root, runs the first
        /// <paramref name="steps"/> of <see cref="CaseParts"/>' steps in it, and disposes everything.
        /// </summary>
        static void RunCase(CaseGraph graph, int steps)
        {
            var scope = graph.Root.CreateChild();
            using (var set = new CaseSet(scope))
            {
                var @case = set.AddCase();
                if (steps >= 2)
                {
                    @case.StageUnion(graph.Ids[0], graph.Ids[1]);
                    @case.EnsureClean();
                }

                if (steps >= 3)
                {
                    @case.StageRow(graph.Mark, [graph.Ids[1], graph.Unit]);
                    @case.EnsureClean();
                }

                if (steps >= 4)
                    @case.RunRules(graph.Copy);

                if (steps >= 5)
                    set.Lift(graph.Spec, LiftOptions.Default);
            }

            scope.Dispose();
        }

        /// <summary>
        /// L7, G2: the late-parent-mint counters.
        /// </summary>
        static void LateParentMints()
        {
            Console.WriteLine();
            Console.WriteLine("## Late parent mints (L7)");
            foreach (var cases in new[] { 2, 8 })
                foreach (var terms in new[] { 16, 256 })
                    Console.WriteLine(LateParentMintBenchmarks.Run(LateParentMintBenchmarks.RootRows, cases, terms));
        }

        /// <summary>
        /// G3: histograms of rows copied per union.
        /// </summary>
        static void CopyVolumes()
        {
            Console.WriteLine();
            Console.WriteLine("## Copy volume per union (G3)");
            foreach (var rows in new[] { 20_000, 200_000 })
            {
                var root = CopyVolumeBenchmarks.Build(rows);
                var pairs = CopyVolumeBenchmarks.Draw(root.Ids, CopyVolumeBenchmarks.Unions);
                var (buckets, total, largest) = CopyVolumeBenchmarks.Histogram(root.Store, pairs);
                Console.WriteLine($"root {rows:N0}: {CopyVolumeBenchmarks.Format(buckets)}; mean {total / (double)pairs.Length:F2}, max {largest}");
            }
        }

        static void Print(string name, double bytes) => Console.WriteLine($"{name,-96} {bytes.ToString("N0", CultureInfo.InvariantCulture),12} B");

        /// <summary>
        /// Gets the mean bytes <paramref name="action"/> allocates on this thread, after a warm-up.
        /// </summary>
        static double Measure(Action action, int warmup = 200, int repetitions = 2000)
        {
            for (var i = 0; i < warmup; i++)
                action();

            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var i = 0; i < repetitions; i++)
                action();

            return (GC.GetAllocatedBytesForCurrentThread() - before) / (double)repetitions;
        }

    }

}

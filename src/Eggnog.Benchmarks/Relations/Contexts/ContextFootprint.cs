using System;
using System.Globalization;

namespace Eggnog.Relations.Benchmarks
{

    /// <summary>
    /// The console app's footprint mode, <c>--context-footprint</c>: prints the bytes the structures of colored
    /// contexts allocate, the bytes of a deep copy of the root, and the copy volume per union, measured in this process.
    /// Allocation counts and counters do not depend on the machine's load, so these figures need no idle machine; times
    /// are not measured.
    /// </summary>
    /// <remarks>
    /// Bytes are <see cref="GC.GetAllocatedBytesForCurrentThread"/> differences after a warm-up, averaged over many
    /// repetitions (the deep copies over three), so they are the memory diagnoser's steady state without the
    /// cold-start allocations (JIT, static initialization) that a dry run's single invocation includes. Every workload
    /// runs serially on the calling thread, and each repetition creates and disposes what it measures, as the matching
    /// benchmark's invocation does.
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
            DeepCopies();
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

            var graph = new BridgeRootWorkload(20_000);
            graph.Root.Freeze();
            Print("bridge child, no layer (EGraphContext.CreateChild, Dispose)", Measure(() => graph.Root.CreateChild().Dispose()));
            foreach (var rows in new[] { 20_000, 200_000 })
            {
                var full = new BridgeRootWorkload(rows, runCopy: true);
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
        /// Bytes per deep copy of the root (<see cref="DeepCopyMemoryBenchmarks"/>).
        /// </summary>
        static void DeepCopies()
        {
            Console.WriteLine();
            Console.WriteLine("## Deep copies: bytes per copy");
            foreach (var rows in new[] { 20_000, 200_000 })
            {
                var graph = new BridgeRootWorkload(rows);
                foreach (var copies in new[] { 2, 8 })
                {
                    var cloned = Measure(() => DeepCopyMemoryBenchmarks.RunClones(graph, copies), warmup: 1, repetitions: 3);
                    Print($"root {rows:N0}, {copies} copies: Database.Clone, per copy", cloned / copies);
                }
            }
        }

        /// <summary>
        /// Histograms of rows copied per union (<see cref="CopyVolumeBenchmarks"/>).
        /// </summary>
        static void CopyVolumes()
        {
            Console.WriteLine();
            Console.WriteLine("## Copy volume per union");
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

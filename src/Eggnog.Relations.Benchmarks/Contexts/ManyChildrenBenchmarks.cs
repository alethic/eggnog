using System;
using System.Threading;
using System.Threading.Tasks;

using BenchmarkDotNet.Attributes;

namespace Eggnog.Relations.Benchmarks
{

    /// <summary>
    /// Many light children of one frozen root (colored-contexts spec §17 M9.4, C14, §14.2): a thousand children, each
    /// making one union and rebuilding, run in parallel and serially over a frozen root of 200,000 rows (the spike's
    /// README reports about 100 ms for a thousand single-merge contexts in parallel on such a root); and the bytes a
    /// child costs with no layer and with one empty layer, whose difference is the bytes per empty layer (spec: about
    /// 1–2 KB).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The root is <see cref="RootWorkload"/>. Child <c>i</c> unions two root ids chosen from a fixed seed, so its
    /// rebuild copies the three root rows naming its loser. Children of a frozen root each have a thread domain of their
    /// own, so the parallel case runs them on the thread pool with no lock but the store's id counter and the child
    /// lists. Every invocation checks that each child saw its union.
    /// </para>
    /// <para>
    /// <see cref="ChildOnly"/> and <see cref="ChildWithOneEmptyLayer"/> each create and dispose one child; the second also
    /// creates the child's layer of a table it never writes (<see cref="ContextTables.GetOrCreateLayer"/>). The memory
    /// diagnoser's allocated bytes per operation of the second minus the first is the cost of one empty layer: an empty
    /// single-shard table, its pending queues and <see cref="TableInfo"/>, and the copy-on-write catalog entry. The class
    /// is not sealed because BenchmarkDotNet generates a class that derives from it.
    /// </para>
    /// </remarks>
    public class ManyChildrenBenchmarks : IItemsPerInvocation
    {

        /// <summary>
        /// The number of children of one invocation of the thousand-children cases.
        /// </summary>
        public const int Children = 1000;

        /// <summary>
        /// The root's row count.
        /// </summary>
        public const int RootRows = 200_000;

        /// <summary>
        /// The seed the unions are drawn from.
        /// </summary>
        public const int Seed = 0x5EED;

        RootWorkload? workload;
        (uint A, uint B)[]? unions;

        /// <summary>
        /// Builds and freezes the root and draws one union per child.
        /// </summary>
        [GlobalSetup]
        public void GlobalSetup()
        {
            workload = new RootWorkload(RootRows);
            var random = new Random(Seed);
            unions = new (uint, uint)[Children];
            for (var i = 0; i < Children; i++)
            {
                var a = workload.Ids[random.Next(RootRows)];
                var b = workload.Ids[random.Next(RootRows)];
                unions[i] = (a, b == a ? workload.Ids[(Array.IndexOf(workload.Ids, a) + 1) % RootRows] : b);
            }
        }

        /// <summary>
        /// A thousand single-union children on the thread pool.
        /// </summary>
        /// <returns>The rows the children copied.</returns>
        [Benchmark]
        public long ThousandChildrenInParallel()
        {
            long copied = 0;
            Parallel.For(0, Children, i => Interlocked.Add(ref copied, RunChild(i)));
            return copied;
        }

        /// <summary>
        /// A thousand single-union children, one after another.
        /// </summary>
        /// <returns>The rows the children copied.</returns>
        [Benchmark]
        public long ThousandChildrenSerially()
        {
            long copied = 0;
            for (var i = 0; i < Children; i++)
                copied += RunChild(i);

            return copied;
        }

        /// <summary>
        /// One child created and disposed, with no layer.
        /// </summary>
        /// <returns>The child's depth.</returns>
        [Benchmark]
        public int ChildOnly()
        {
            var child = workload!.Store.Root.CreateChild();
            var depth = child.Depth;
            child.Dispose();
            return depth;
        }

        /// <summary>
        /// One child created and disposed, with one empty layer.
        /// </summary>
        /// <returns>The layers the child created.</returns>
        [Benchmark]
        public long ChildWithOneEmptyLayer()
        {
            var child = workload!.Store.Root.CreateChild();
            child.Tables!.GetOrCreateLayer(workload.F);
            var layers = child.Tables.Statistics.LayersCreated;
            child.Dispose();
            return layers;
        }

        /// <summary>
        /// Child <paramref name="index"/>: creation, one union, a rebuild, a check, disposal.
        /// </summary>
        /// <returns>The rows the child copied.</returns>
        /// <exception cref="InvalidOperationException">The child does not see its union.</exception>
        long RunChild(int index)
        {
            var (a, b) = unions![index];
            var child = workload!.Store.Root.CreateChild();
            child.Union(a, b);
            child.Rebuild();
            if (child.Find(a) != child.Find(b))
                throw new InvalidOperationException($"Child {index} does not see its union of {a} and {b}.");

            var copied = child.Tables!.Statistics.RowsCopied;
            child.Dispose();
            return copied;
        }

        /// <inheritdoc />
        /// <remarks>
        /// The children of the thousand-children cases; one child for the others.
        /// </remarks>
        long IItemsPerInvocation.ItemsPerInvocation(string benchmarkMethod) => benchmarkMethod switch
        {
            nameof(ThousandChildrenInParallel) or nameof(ThousandChildrenSerially) => Children,
            _ => 1,
        };

    }

}

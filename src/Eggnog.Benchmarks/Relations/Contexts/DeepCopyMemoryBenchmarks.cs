using BenchmarkDotNet.Attributes;

namespace Eggnog.Relations.Benchmarks
{

    /// <summary>
    /// Memory of deep copies of the root: the bytes <see cref="Copies"/> copies of a root allocate, each a
    /// <see cref="Database.Clone"/> of the root database with one union and one fact merged into it, over roots of
    /// 20,000 and 200,000 ids.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The root is a <see cref="BridgeRootWorkload"/>: <c>edge(x_i, x_{i+1})</c> for every id and <c>mark</c> for every
    /// tenth, left writable (a root with live children cannot be cloned). Copy <c>j</c> stages the union of
    /// <c>x_{2j}</c> with <c>x_{2j+1}</c> and the fact <c>mark(x_1)</c>, and merges them.
    /// </para>
    /// <para>
    /// The memory diagnoser's allocated bytes per operation, divided by <see cref="Copies"/>, are the bytes per copy,
    /// linear in the root's size. <c>--context-footprint</c> prints the steady-state figures without the dry run's
    /// cold-start allocations. The class is not sealed because BenchmarkDotNet generates a class that derives from it.
    /// </para>
    /// </remarks>
    public class DeepCopyMemoryBenchmarks : IItemsPerInvocation
    {

        BridgeRootWorkload? root;

        /// <summary>
        /// Gets or sets the number of root ids.
        /// </summary>
        [Params(20_000, 200_000)]
        public int RootRows { get; set; }

        /// <summary>
        /// Gets or sets the number of copies one invocation makes.
        /// </summary>
        [Params(2, 8)]
        public int Copies { get; set; }

        /// <summary>
        /// Builds the root.
        /// </summary>
        [GlobalSetup]
        public void GlobalSetup()
        {
            root = new BridgeRootWorkload(RootRows);
        }

        /// <summary>
        /// One clone of the root per copy, with the copy's writes merged into it.
        /// </summary>
        /// <returns>The rows the copies hold.</returns>
        [Benchmark]
        public long CloneRoot() => RunClones(root!, Copies);

        /// <summary>
        /// Clones <paramref name="graph"/>'s root database <paramref name="copies"/> times, stages the copy's union and
        /// fact into each and merges them.
        /// </summary>
        /// <param name="graph">The graph whose root database is cloned; its root must have no live children.</param>
        /// <param name="copies">The number of clones.</param>
        /// <returns>The rows the copies hold.</returns>
        internal static long RunClones(BridgeRootWorkload graph, int copies)
        {
            var source = graph.EGraph.Database;
            var unionFind = graph.EGraph.Store.UnionFindTable;
            var mark = graph.EGraph.GetTable(graph.Mark);
            long rows = 0;
            for (var j = 0; j < copies; j++)
            {
                var copy = source.Clone();
                var timestamp = graph.EGraph.Store.ReadClock();
                var state = copy.CreateExecutionState(timestamp);
                state.StageInsert(unionFind, [graph.Ids[2 * j].Rep, graph.Ids[2 * j + 1].Rep, timestamp]);
                state.StageInsert(mark, [graph.Ids[1].Rep, graph.Unit.Rep, timestamp]);
                state.Flush();
                copy.MergeAll(timestamp);
                rows += copy.TotalSizeEstimate;
            }

            return rows;
        }

        /// <inheritdoc />
        /// <remarks>
        /// The copies one invocation makes.
        /// </remarks>
        long IItemsPerInvocation.ItemsPerInvocation(string benchmarkMethod) => Copies;

    }

}

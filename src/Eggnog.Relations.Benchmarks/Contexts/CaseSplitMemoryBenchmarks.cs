using System;

using BenchmarkDotNet.Attributes;

namespace Eggnog.Relations.Benchmarks
{

    /// <summary>
    /// Memory of a case split, contexts against full clones (colored-contexts spec §17 M10.4, L6, B5): the bytes one split
    /// of <see cref="Cases"/> cases allocates with colored contexts (a case set under a fresh scope, each case one union
    /// and one fact, optionally lifted), against egglog's push, a <see cref="Database.Clone"/> of the root per case with
    /// the same writes (the in-house baseline), over roots of 20,000 and 200,000 ids.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The root is a <see cref="CaseGraph"/>: <c>edge(x_i, x_{i+1})</c> for every id and <c>mark</c> for every tenth. The
    /// context side freezes it and opens each split under a fresh writable child (the scope), so the scope's own state
    /// does not grow across invocations; case <c>j</c> unions <c>x_{2j}</c> with <c>x_{2j+1}</c> and marks
    /// <c>x_1</c>, which every case shares, so the lift has one fact to write. The clone side clones a second, unfrozen
    /// root with the same rows (a root with live children cannot be cloned, §4.3), stages the same union and fact into each
    /// copy and merges them; it never lifts, since clones have nothing to lift into.
    /// </para>
    /// <para>
    /// The memory diagnoser's allocated bytes per operation, divided by <see cref="Cases"/>, are the bytes per case: flat
    /// in the root's size for contexts, linear for clones. <c>--context-footprint</c> prints the steady-state figures
    /// without the dry run's cold-start allocations. The class is not sealed because BenchmarkDotNet generates a class
    /// that derives from it.
    /// </para>
    /// </remarks>
    public class CaseSplitMemoryBenchmarks : IItemsPerInvocation
    {

        CaseGraph? contexts;
        CaseGraph? clones;

        /// <summary>
        /// Gets or sets the number of root ids.
        /// </summary>
        [Params(20_000, 200_000)]
        public int RootRows { get; set; }

        /// <summary>
        /// Gets or sets the number of cases of the split.
        /// </summary>
        [Params(2, 8)]
        public int Cases { get; set; }

        /// <summary>
        /// Builds the two roots and freezes the one the contexts split under.
        /// </summary>
        [GlobalSetup]
        public void GlobalSetup()
        {
            contexts = new CaseGraph(RootRows);
            contexts.Root.Freeze();
            clones = new CaseGraph(RootRows);
        }

        /// <summary>
        /// The baseline: one clone of the root per case, with the case's writes merged into it.
        /// </summary>
        /// <returns>The rows the copies hold.</returns>
        [Benchmark(Baseline = true)]
        public long CloneCases() => RunClones(clones!, Cases);

        /// <summary>
        /// One split under a fresh scope: the cases, their writes and rebuilds, disposal.
        /// </summary>
        /// <returns>The rows the cases hold themselves.</returns>
        [Benchmark]
        public long ContextCases() => RunContexts(contexts!, Cases, lift: false);

        /// <summary>
        /// One split under a fresh scope, lifted before disposal.
        /// </summary>
        /// <returns>The rows the cases hold themselves.</returns>
        [Benchmark]
        public long ContextCasesWithLift() => RunContexts(contexts!, Cases, lift: true);

        /// <summary>
        /// Opens <paramref name="cases"/> cases under a fresh child of <paramref name="graph"/>'s frozen root, writes and
        /// rebuilds each, lifts if asked, and disposes everything.
        /// </summary>
        /// <returns>The rows the cases hold themselves.</returns>
        internal static long RunContexts(CaseGraph graph, int cases, bool lift)
        {
            var scope = graph.Root.CreateChild();
            long rows = 0;
            using (var set = new CaseSet(scope))
            {
                for (var j = 0; j < cases; j++)
                {
                    var @case = set.AddCase();
                    @case.StageUnion(graph.Ids[2 * j], graph.Ids[2 * j + 1]);
                    @case.StageRow(graph.Mark, [graph.Ids[1], graph.Unit]);
                    @case.EnsureClean();
                    rows += @case.OwnRowCount;
                }

                if (lift)
                    set.Lift(graph.Spec, LiftOptions.Default);
            }

            scope.Dispose();
            return rows;
        }

        /// <summary>
        /// Clones <paramref name="graph"/>'s root database once per case, stages the case's union and fact into the copy and
        /// merges them.
        /// </summary>
        /// <returns>The rows the copies hold.</returns>
        internal static long RunClones(CaseGraph graph, int cases)
        {
            var source = graph.EGraph.Database;
            var unionFind = graph.EGraph.Store.UnionFindTable;
            var mark = graph.EGraph.GetTable(graph.Mark);
            long rows = 0;
            for (var j = 0; j < cases; j++)
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
        /// The cases of one split.
        /// </remarks>
        long IItemsPerInvocation.ItemsPerInvocation(string benchmarkMethod) => Cases;

    }

}

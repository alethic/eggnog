using System;

using BenchmarkDotNet.Attributes;

namespace Eggnog.Relations.Benchmarks
{

    /// <summary>
    /// What one child costs, as its root grows (colored-contexts spec §17 M9.4, the storage half of L6 and B4): a child
    /// is created, makes 100 unions in one write batch, rebuilds and is disposed, over roots of 20,000, 200,000 and
    /// 2,000,000 rows, frozen or not. Time and allocated bytes per invocation must stay flat within 1.5× across the three
    /// sizes (the M9.4 done-when); the child's copy and examination counters are identical across them, which the
    /// setup checks.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The root is <see cref="RootWorkload"/>: each id is named by one <c>f</c> row and two <c>r</c> rows. The
    /// child unions the first 200 ids in pairs, so 100 of them become its own losers; its rebuild finds the 300 root rows
    /// naming them through the root's rebuild indexes (never a scan), copies them canonically, hides the originals and
    /// merges. A frozen root built its indexes at its freeze; a root that is not frozen brings them up to date when it
    /// creates a child (root work, which the setup's first child absorbs; later children find them current). M10.4
    /// extends the workload with a rule run (the full half).
    /// </para>
    /// <para>
    /// Each invocation creates and disposes its own child, so BenchmarkDotNet chooses the invocation count; the memory
    /// diagnoser's allocated bytes per operation are the per-child bytes. The class is not sealed because
    /// BenchmarkDotNet generates a class that derives from it.
    /// </para>
    /// </remarks>
    public class PerChildCostBenchmarks : IItemsPerInvocation
    {

        /// <summary>
        /// The unions one child makes.
        /// </summary>
        public const int Unions = 100;

        RootWorkload? workload;

        /// <summary>
        /// Gets or sets the number of rows of each root table.
        /// </summary>
        [Params(20_000, 200_000, 2_000_000)]
        public int RootRows { get; set; }

        /// <summary>
        /// Gets or sets whether the root is frozen.
        /// </summary>
        [Params(true, false)]
        public bool FrozenRoot { get; set; }

        /// <summary>
        /// Builds (and freezes) the root, and checks that one child does exactly the expected work.
        /// </summary>
        /// <exception cref="InvalidOperationException">The child examined or copied other rows than the 300 naming its
        /// losers, or caught up rows of the root.</exception>
        [GlobalSetup]
        public void GlobalSetup()
        {
            workload = new RootWorkload(RootRows, FrozenRoot);
            var (examined, copied, caughtUp) = Run(workload);
            if (examined != 3 * Unions || copied != 3 * Unions || caughtUp != 0)
                throw new InvalidOperationException($"A child examined {examined}, copied {copied} and caught up {caughtUp} rows; expected {3 * Unions}, {3 * Unions} and 0.");
        }

        /// <summary>
        /// One child: creation, 100 unions, a rebuild, disposal.
        /// </summary>
        /// <returns>The ancestor rows the child copied.</returns>
        [Benchmark]
        public long ChildUnionsAndRebuild() => Run(workload!).Copied;

        /// <summary>
        /// Runs one child over <paramref name="root"/> and returns its counters.
        /// </summary>
        static (long Examined, long Copied, long CaughtUp) Run(RootWorkload root)
        {
            var child = root.Store.Root.CreateChild();
            child.Write((root.Ids, root.Store.UnionFindTable), static (state, args) =>
            {
                Span<uint> row = stackalloc uint[3];
                for (var i = 0; i < Unions; i++)
                {
                    row[0] = args.Ids[2 * i];
                    row[1] = args.Ids[2 * i + 1];
                    row[2] = state.Timestamp;
                    state.StageInsert(args.UnionFindTable, row);
                }

                return 0;
            });
            child.Rebuild();
            var statistics = child.Tables!.Statistics;
            var result = (statistics.AncestorRowsExamined, statistics.RowsCopied, statistics.CatchUpRowsExamined);
            child.Dispose();
            return result;
        }

        /// <inheritdoc />
        /// <remarks>
        /// The unions one child makes.
        /// </remarks>
        long IItemsPerInvocation.ItemsPerInvocation(string benchmarkMethod) => benchmarkMethod == nameof(ChildUnionsAndRebuild) ? Unions : 0;

    }

}

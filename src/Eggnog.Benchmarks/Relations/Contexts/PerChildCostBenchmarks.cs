using System;

using BenchmarkDotNet.Attributes;

namespace Eggnog.Relations.Benchmarks
{

    /// <summary>
    /// What one child costs, as its root grows (colored-contexts spec §17 M9.4 and M10.4, L6 and B4): a child is created,
    /// makes 100 unions, rebuilds and is disposed, over roots of 20,000, 200,000 and 2,000,000 rows, frozen or not; the
    /// full workload (M10.4) also runs one rule in the child. Time and allocated bytes per invocation must stay flat
    /// within 1.5× across the three sizes (the M9.4 done-when); the child's copy, examination and match counters are
    /// identical across them, which the setups check.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The root is <see cref="RootWorkload"/>: each id is named by one <c>f</c> row and two <c>r</c> rows. The
    /// child unions the first 200 ids in pairs, so 100 of them become its own losers; its rebuild finds the 300 root rows
    /// naming them through the root's rebuild indexes (never a scan), copies them canonically, hides the originals and
    /// merges. A frozen root built its indexes at its freeze; a root that is not frozen brings them up to date when it
    /// creates a child (root work, which the setup's first child absorbs; later children find them current).
    /// </para>
    /// <para>
    /// The full workload (<see cref="ChildUnionsRebuildAndRuleRun"/>) runs on the bridge: a <see cref="CaseGraph"/> root
    /// of <see cref="RootRows"/> ids whose rule <c>edge(x, y) ⇒ path(x, y)</c> ran once at the root. The child stages the
    /// same 100 unions, flushes (its rebuild copies the root rows naming its losers) and runs the rule once. The rule's
    /// watermark is inherited from the root (§10.2), so its semi-naive variants match only the child's 200 rewritten
    /// <c>edge</c> rows, whatever the root's size; the setup checks the match count.
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
        CaseGraph? graph;

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
        [GlobalSetup(Target = nameof(ChildUnionsAndRebuild))]
        public void GlobalSetup()
        {
            workload = new RootWorkload(RootRows, FrozenRoot);
            var (examined, copied, caughtUp) = Run(workload);
            if (examined != 3 * Unions || copied != 3 * Unions || caughtUp != 0)
                throw new InvalidOperationException($"A child examined {examined}, copied {copied} and caught up {caughtUp} rows; expected {3 * Unions}, {3 * Unions} and 0.");
        }

        /// <summary>
        /// Builds the bridge root, runs the rule there once, freezes the root when asked, and checks that one child's run
        /// matches exactly its rewritten rows.
        /// </summary>
        /// <exception cref="InvalidOperationException">The child's run matched other rows than the 200 it rewrote.</exception>
        [GlobalSetup(Target = nameof(ChildUnionsRebuildAndRuleRun))]
        public void GlobalSetupFull()
        {
            graph = new CaseGraph(RootRows, runCopy: true);
            if (FrozenRoot)
                graph.Root.Freeze();

            var matches = RunFull(graph);
            if (matches != 2 * Unions)
                throw new InvalidOperationException($"A child's rule run matched {matches} rows; expected {2 * Unions}.");
        }

        /// <summary>
        /// One child: creation, 100 unions, a rebuild, disposal.
        /// </summary>
        /// <returns>The ancestor rows the child copied.</returns>
        [Benchmark]
        public long ChildUnionsAndRebuild() => Run(workload!).Copied;

        /// <summary>
        /// The full workload (M10.4): a bridge child, 100 unions, a flush (which rebuilds), one rule run, disposal.
        /// </summary>
        /// <returns>The rule's matches.</returns>
        [Benchmark]
        public long ChildUnionsRebuildAndRuleRun() => RunFull(graph!);

        /// <summary>
        /// Runs the full workload once over <paramref name="root"/> and returns the rule's matches.
        /// </summary>
        static long RunFull(CaseGraph root)
        {
            var child = root.Root.CreateChild();
            for (var i = 0; i < Unions; i++)
                child.StageUnion(root.Ids[2 * i], root.Ids[2 * i + 1]);

            child.FlushUpdates();
            var report = child.RunRules(root.Copy);
            var matches = report.RuleSetReport.NumMatches("edge-path");
            child.Dispose();
            return matches;
        }

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
        long IItemsPerInvocation.ItemsPerInvocation(string benchmarkMethod) => benchmarkMethod is nameof(ChildUnionsAndRebuild) or nameof(ChildUnionsRebuildAndRuleRun) ? Unions : 0;

    }

}

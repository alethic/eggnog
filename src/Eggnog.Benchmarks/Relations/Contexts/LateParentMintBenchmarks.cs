using System;
using System.Collections.Generic;

using BenchmarkDotNet.Attributes;

namespace Eggnog.Relations.Benchmarks
{

    /// <summary>
    /// Late parent mints: a parent that is not frozen lifts terms after its cases built them, so each lifted term gets
    /// a parent id larger than the cases' local ids for the same term, and smaller-id-wins makes the parent id lose in
    /// every case. The workload reports what that costs: the cases' parent-id losers
    /// (<see cref="UnionFindStatistics.ParentIdLosers"/>), the rows they copy per lift round, and the growth of the
    /// root's id array caused by the cases' reserved id blocks.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The parent is the writable root of a <see cref="CaseGraph"/> of <see cref="RootRows"/> ids. Each of
    /// <see cref="Cases"/> cases builds <c>num(0) … num(T−1)</c> and the chain <c>add(num(i), num(i+1))</c> from local ids,
    /// and unions the last <c>add</c> and <c>num(T−1)</c> with <c>num(0)</c>. The lift then takes one round per level: the
    /// <c>num</c> terms (base keys), then the <c>add</c> terms over them and the equality of the lifted <c>num</c> ids,
    /// which the root unions. Each round is a separate one-round lift, so
    /// the rows the cases copy can be read per round.
    /// </para>
    /// <para>
    /// A lifted term's parent id loses to the case's local id in every case (one parent-id loser per term per case), and
    /// the case then copies the parent's rows naming the loser. The root's union-find array grows to the largest id the
    /// root unions, here a lifted term minted above every reserved block. These are counters: they need no idle machine.
    /// Each invocation builds its own graph, so its time and bytes include the root's construction. The class is not
    /// sealed because BenchmarkDotNet generates a class that derives from it.
    /// </para>
    /// </remarks>
    public class LateParentMintBenchmarks : IItemsPerInvocation
    {

        /// <summary>
        /// The root's size.
        /// </summary>
        public const int RootRows = 10_000;

        /// <summary>
        /// Gets or sets the number of cases.
        /// </summary>
        [Params(2, 8)]
        public int Cases { get; set; }

        /// <summary>
        /// Gets or sets the <c>num</c> terms each case builds.
        /// </summary>
        [Params(16, 256)]
        public int Terms { get; set; }

        /// <summary>
        /// Runs the workload once and prints its counters.
        /// </summary>
        [GlobalSetup]
        public void GlobalSetup()
        {
            Console.WriteLine($"// LateParentMint {Run(RootRows, Cases, Terms)}");
        }

        /// <summary>
        /// One workload: the root, the cases, the lift round by round, disposal.
        /// </summary>
        /// <returns>The parent-id losers.</returns>
        [Benchmark]
        public long BuildCasesAndLift() => Run(RootRows, Cases, Terms).ParentIdLosers;

        /// <summary>
        /// Runs the workload and returns its counters.
        /// </summary>
        /// <param name="rootRows">The root's ids.</param>
        /// <param name="cases">The cases.</param>
        /// <param name="terms">The <c>num</c> terms per case.</param>
        /// <returns>The counters.</returns>
        /// <exception cref="InvalidOperationException">The lift did not reach the expected state.</exception>
        internal static LateParentMintResult Run(int rootRows, int cases, int terms)
        {
            var graph = new CaseGraph(rootRows);
            var root = graph.Root;
            var rootIds = graph.EGraph.Store.RootUnionFind.UnionFind.UnionFind;
            using var set = new CaseSet(root);
            for (var c = 0; c < cases; c++)
            {
                var @case = set.AddCase();
                var nums = new Value[terms];
                for (var i = 0; i < terms; i++)
                    nums[i] = @case.StageTerm(graph.Num, [graph.Int(i)]);

                var last = nums[0];
                for (var i = 0; i + 1 < terms; i++)
                    last = @case.StageTerm(graph.Add, [nums[i], nums[i + 1]]);

                @case.StageUnion(last, nums[0]);
                @case.StageUnion(nums[terms - 1], nums[0]);
                @case.EnsureClean();
            }

            var before = rootIds.IdCount;
            var copies = new List<long>();
            var copied = CopiedByCases(set);
            var rounds = 0;
            var lifted = 0;
            var oneRound = new LiftOptions { RoundLimit = 1 };
            while (true)
            {
                var report = set.Lift(graph.Spec, oneRound);
                if (report.Changed == false)
                    break;

                rounds += report.Rounds;
                lifted += report.Terms;
                var now = CopiedByCases(set);
                copies.Add(now - copied);
                copied = now;
            }

            if (root.LookupId(graph.Num, [graph.Int(terms - 1)]) is not { } top)
                throw new InvalidOperationException("The num terms were not lifted.");

            long losers = 0;
            long reserved = 0;
            foreach (var @case in set.Cases)
            {
                losers += @case.Context.UnionFindStatistics.ParentIdLosers;
                var blocks = @case.Context.IdBlocks!;
                for (var b = 0; b < blocks.BlockCount; b++)
                    reserved += blocks.BlockEnd(b) - blocks.BlockStart(b);
            }

            GC.KeepAlive(top);
            return new LateParentMintResult
            {
                Cases = cases,
                Terms = terms,
                Rounds = rounds,
                TermsLifted = lifted,
                ParentIdLosers = losers,
                CopiesPerRound = copies,
                RootMinted = rootRows + lifted,
                CaseReserved = reserved,
                RootIdArrayBefore = before,
                RootIdArrayAfter = rootIds.IdCount,
            };
        }

        /// <summary>
        /// Gets the ancestor rows the cases of <paramref name="set"/> have copied so far.
        /// </summary>
        static long CopiedByCases(CaseSet set)
        {
            long total = 0;
            foreach (var @case in set.Cases)
                total += @case.Context.Tables!.Statistics.RowsCopied;

            return total;
        }

        /// <inheritdoc />
        /// <remarks>
        /// The terms all cases build: <c>Cases × (2 × Terms − 1)</c>.
        /// </remarks>
        long IItemsPerInvocation.ItemsPerInvocation(string benchmarkMethod) => Cases * (2L * Terms - 1);

    }

}

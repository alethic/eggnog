using System;

using BenchmarkDotNet.Attributes;

namespace Eggnog.Relations.Benchmarks
{

    /// <summary>
    /// Catch-up under a busy parent: a writable root with <see cref="Children"/> live children, each of which made one
    /// union of its own, appends <see cref="Appended"/> rows to a shared table, and every child then rebuilds, reading
    /// each appended row once. The children examine <c>Children × Appended</c> rows in all, whatever the root's size;
    /// the tenth of the appended rows that name a child's loser are copied (canonically) by that child alone. Time per
    /// invocation divided by <see cref="IItemsPerInvocation.ItemsPerInvocation"/> is the time per examined row.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The root is a <see cref="RootWorkload"/> of <see cref="RootRows"/> ids that is not frozen. Child <c>i</c> unions
    /// <c>Ids[2i]</c> and <c>Ids[2i + 1]</c>; the smaller id wins, so its loser is <c>Ids[2i + 1]</c>, and its first
    /// rebuild copies the three root rows naming it. The root then appends rows to <c>r</c>: row <c>j</c> is
    /// <c>r(Ids[1000 + j], Ids[1500 + j])</c>, except that every tenth row (<c>j</c> a multiple of 10) names a loser,
    /// <c>r(loser of child (j / 10) mod Children, Ids[1500 + j])</c>. No key repeats a row of the root, so the root's
    /// <c>r</c> layer grows by exactly <see cref="Appended"/> rows. A child's catch-up reads all of them (one
    /// <c>HiddenBetween</c> test, one find per rebuild column and one own-layer lookup each) and copies only those
    /// naming its own loser; every other id is canonical in it.
    /// </para>
    /// <para>
    /// The appends change the root, so each iteration builds a fresh root and children, appends, and measures only the
    /// children's rebuilds (<c>[IterationSetup]</c>, <c>[InvocationCount(1)]</c>). After each iteration the counters
    /// must equal the formula exactly: examined <c>Children × Appended</c>, copied <c>Appended / 10</c>, and each
    /// child must see the canonical copy of every appended row naming its loser; then the children are disposed, and the
    /// root must have no live child left. Each iteration's setup checks that the previous iteration was checked. An
    /// iteration is one invocation, far
    /// below BenchmarkDotNet's recommended 100 ms in the cases with 100 appended rows, so the time per examined row is
    /// read from the cases with 10,000; the smaller cases are kept for their counters. The class is not sealed because
    /// BenchmarkDotNet generates a class that derives from it.
    /// </para>
    /// </remarks>
    [InvocationCount(1)]
    public class BusyParentCatchUpBenchmarks : IItemsPerInvocation
    {

        /// <summary>
        /// The root's ids, and its rows per table: enough for the largest append's ids (<c>1500 + Appended</c>).
        /// </summary>
        public const int RootRows = 20_000;

        /// <summary>
        /// One appended row in this many names a child's loser.
        /// </summary>
        public const int LoserEvery = 10;

        RootWorkload? workload;
        Context[]? children;
        long examinedBefore;
        long copiedBefore;
        bool pendingCheck;

        /// <summary>
        /// Gets or sets the number of live children.
        /// </summary>
        [Params(1, 8, 64)]
        public int Children { get; set; }

        /// <summary>
        /// Gets or sets the number of rows the root appends while the children live.
        /// </summary>
        [Params(100, 10_000)]
        public int Appended { get; set; }

        /// <summary>
        /// Runs the workload once, checks its counters and prints them.
        /// </summary>
        /// <exception cref="InvalidOperationException">The counters differ from the formula.</exception>
        [GlobalSetup]
        public void GlobalSetup()
        {
            IterationSetup();
            CatchUp();
            var (examined, copied) = Check();
            Console.WriteLine($"// BusyParentCatchUp children {Children}, appended {Appended}: examined {examined}, copied {copied}");
        }

        /// <summary>
        /// Builds the root and its children, gives each child its union and first rebuild, and appends to the root.
        /// </summary>
        /// <exception cref="InvalidOperationException">The previous iteration was not checked.</exception>
        [IterationSetup]
        public void IterationSetup()
        {
            if (pendingCheck)
                throw new InvalidOperationException("The previous iteration was not checked: the iteration cleanup did not run.");

            workload = new RootWorkload(RootRows, freeze: false);
            var root = workload.Store.Root;
            var ids = workload.Ids;
            var unionFind = workload.Store.UnionFindTable;
            children = new Context[Children];
            for (var i = 0; i < children.Length; i++)
            {
                var child = root.CreateChild();
                var (winner, loser) = (ids[2 * i], ids[2 * i + 1]);
                child.Write(state =>
                {
                    state.StageInsert(unionFind, [winner, loser, state.Timestamp]);
                    return 0;
                });
                child.Rebuild();
                children[i] = child;
            }

            var r = workload.R;
            var count = Children;
            var appended = Appended;
            root.Write(state =>
            {
                for (var j = 0; j < appended; j++)
                {
                    var x = j % LoserEvery == 0 ? ids[2 * (j / LoserEvery % count) + 1] : ids[1000 + j];
                    state.StageInsert(r, [x, ids[1500 + j], state.Timestamp]);
                }

                return 0;
            });

            (examinedBefore, copiedBefore) = Totals();
            pendingCheck = true;
        }

        /// <summary>
        /// Every child rebuilds, catching up with the root's appends.
        /// </summary>
        /// <returns>The children whose rebuild changed them.</returns>
        [Benchmark]
        public int CatchUp()
        {
            var changed = 0;
            foreach (var child in children!)
                if (child.Rebuild())
                    changed++;

            return changed;
        }

        /// <summary>
        /// Checks the iteration's counters and copies, and disposes the children.
        /// </summary>
        /// <exception cref="InvalidOperationException">The counters differ from the formula, a child does not see a
        /// copy it must hold, or the root has a live child left.</exception>
        [IterationCleanup]
        public void IterationCleanup() => Check();

        /// <summary>
        /// Checks the counters since <see cref="IterationSetup"/> against the formula and each child's copies, then
        /// disposes the children, and checks that the root has no live child left.
        /// </summary>
        /// <returns>The rows the children examined and copied.</returns>
        (long Examined, long Copied) Check()
        {
            pendingCheck = false;
            var (examined, copied) = Totals();
            examined -= examinedBefore;
            copied -= copiedBefore;
            if (examined != (long)Children * Appended || copied != Appended / LoserEvery)
                throw new InvalidOperationException($"The children examined {examined} and copied {copied} rows; expected {(long)Children * Appended} and {Appended / LoserEvery}.");

            var ids = workload!.Ids;
            Span<uint> row = stackalloc uint[3];
            for (var j = 0; j < Appended; j += LoserEvery)
            {
                var i = j / LoserEvery % Children;
                if (children![i].TryGetRow(workload.R, [ids[2 * i], ids[1500 + j]], row) == false)
                    throw new InvalidOperationException($"Child {i} does not see the canonical copy of appended row {j}.");
            }

            for (var i = children!.Length - 1; i >= 0; i--)
                children[i].Dispose();

            if (workload.Store.Root.ChildCount is var live && live != 0)
                throw new InvalidOperationException($"The root has {live} live children after every child was disposed.");

            return (examined, copied);
        }

        /// <summary>
        /// Gets the rows every child has examined in catch-ups and copied, so far.
        /// </summary>
        (long Examined, long Copied) Totals()
        {
            long examined = 0, copied = 0;
            foreach (var child in children!)
            {
                var statistics = child.Tables!.Statistics;
                examined += statistics.CatchUpRowsExamined;
                copied += statistics.RowsCopied;
            }

            return (examined, copied);
        }

        /// <inheritdoc />
        /// <remarks>
        /// The rows the children examine in all: <c>Children × Appended</c>.
        /// </remarks>
        long IItemsPerInvocation.ItemsPerInvocation(string benchmarkMethod) => (long)Children * Appended;

    }

}

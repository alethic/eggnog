using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

using BenchmarkDotNet.Attributes;

namespace Eggnog.Relations.Benchmarks
{

    /// <summary>
    /// Copy volume per union: how many ancestor rows a child copies (rewrites into its own layer, hiding the originals)
    /// for one union, as a histogram over unions of random root ids on a root whose ids are named by very different
    /// numbers of rows.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The root (<see cref="Build"/>) holds, for each of its ids, one <c>f(x) → v</c> row and one <c>r(x, h(x))</c>
    /// row, where the hub <c>h(x)</c> is drawn with a heavy tail (the cube of a uniform draw over a random permutation
    /// of the ids, so a few random ids are the hub of thousands of rows), and it is frozen. A union copies the rows
    /// naming its loser, the larger id (smaller-id-wins), so a union whose loser is a hub copies thousands of rows and
    /// most copy two or three. Only losers are marked, so nothing is copied for the winner's rows: the lift reads those
    /// through the winner probe (<see cref="Context.ScanAncestorsMentioning{TSink}"/>) instead.
    /// </para>
    /// <para>
    /// <see cref="Histogram"/> runs <see cref="Unions"/> single-union children and buckets the rows each copied (powers of
    /// two); the setup prints it. The benchmark runs those unions in one child, a rebuild after each. Counters do not
    /// depend on the machine's load; times do. The class is not sealed because BenchmarkDotNet generates a class that
    /// derives from it.
    /// </para>
    /// </remarks>
    public class CopyVolumeBenchmarks : IItemsPerInvocation
    {

        /// <summary>
        /// The unions per invocation and in the histogram.
        /// </summary>
        public const int Unions = 1000;

        /// <summary>
        /// The seed of the hubs and the unions.
        /// </summary>
        public const int Seed = 0xC0F1;

        (Store Store, TableId F, TableId R, uint[] Ids)? root;
        (uint A, uint B)[]? pairs;

        /// <summary>
        /// Gets or sets the number of root ids.
        /// </summary>
        [Params(20_000, 200_000)]
        public int RootRows { get; set; }

        /// <summary>
        /// Builds and freezes the root, draws the unions, and prints the histogram of rows copied per union.
        /// </summary>
        [GlobalSetup]
        public void GlobalSetup()
        {
            root = Build(RootRows);
            pairs = Draw(root.Value.Ids, Unions);
            var (histogram, total, largest) = Histogram(root.Value.Store, pairs);
            Console.WriteLine($"// CopyVolume RootRows={RootRows}: {Format(histogram)}; mean {total / (double)Unions:F2}, max {largest}");
        }

        /// <summary>
        /// <see cref="Unions"/> unions in one child, each followed by a rebuild.
        /// </summary>
        /// <returns>The rows the child copied.</returns>
        [Benchmark]
        public long UnionsInOneChild()
        {
            var (store, _, _, _) = root!.Value;
            var child = store.Root.CreateChild();
            foreach (var (a, b) in pairs!)
            {
                child.Union(a, b);
                child.Rebuild();
            }

            var copied = child.Tables!.Statistics.RowsCopied;
            child.Dispose();
            return copied;
        }

        /// <summary>
        /// Builds a frozen root of <paramref name="rows"/> ids with a heavy-tailed <c>r</c>.
        /// </summary>
        /// <param name="rows">The number of ids.</param>
        /// <returns>The store, its tables and the ids, ascending.</returns>
        internal static (Store Store, TableId F, TableId R, uint[] Ids) Build(int rows)
        {
            var store = new Store(ParallelHeuristics.Serial);
            var key = new ColumnId(0);
            var f = store.AddTable(new SortedWritesTable(1, 3, new ColumnId(2), [key], new MaxMerge(new ColumnId(1)), ParallelHeuristics.Serial), name: "f");
            var r = store.AddTable(new SortedWritesTable(2, 3, new ColumnId(2), [key, new ColumnId(1)], new MinMerge(new ColumnId(2)), ParallelHeuristics.Serial), name: "r");
            var ids = new uint[rows];
            for (var i = 0; i < rows; i++)
                ids[i] = store.Root.NewId();

            // Hub ranks are drawn with a heavy tail and mapped through a random permutation, so the busiest hubs are random
            // ids: about half of them lose a union to a smaller id.
            var random = new Random(Seed);
            var permutation = (uint[])ids.Clone();
            random.Shuffle(permutation);
            var hubs = new uint[rows];
            for (var i = 0; i < rows; i++)
            {
                var u = random.NextDouble();
                hubs[i] = permutation[(int)(u * u * u * rows)];
            }

            store.Root.Write((ids, hubs, f, r), static (state, args) =>
            {
                for (var i = 0; i < args.ids.Length; i++)
                {
                    state.StageInsert(args.f, [args.ids[i], (uint)(i % 7), state.Timestamp]);
                    state.StageInsert(args.r, [args.ids[i], args.hubs[i], state.Timestamp]);
                }

                return 0;
            });
            store.Root.Freeze();
            return (store, f, r, ids);
        }

        /// <summary>
        /// Draws <paramref name="count"/> unions of distinct random ids.
        /// </summary>
        /// <param name="ids">The ids to draw from.</param>
        /// <param name="count">The number of unions.</param>
        /// <returns>The unions, each of two different ids.</returns>
        internal static (uint A, uint B)[] Draw(uint[] ids, int count)
        {
            var random = new Random(Seed + 1);
            var pairs = new (uint, uint)[count];
            for (var i = 0; i < count; i++)
            {
                var a = ids[random.Next(ids.Length)];
                var b = ids[random.Next(ids.Length)];
                pairs[i] = (a, a == b ? ids[(Array.IndexOf(ids, a) + 1) % ids.Length] : b);
            }

            return pairs;
        }

        /// <summary>
        /// Runs each union in a child of its own and buckets the rows the child copied: bucket 0 holds unions that copied
        /// nothing, bucket <c>k ≥ 1</c> those that copied <c>[2^(k-1), 2^k)</c> rows.
        /// </summary>
        /// <param name="store">The store whose frozen root the children are created under.</param>
        /// <param name="pairs">The unions, one child each.</param>
        /// <returns>The bucket counts, the total rows copied and the largest copy.</returns>
        internal static (long[] Buckets, long Total, long Largest) Histogram(Store store, (uint A, uint B)[] pairs)
        {
            var buckets = new long[33];
            long total = 0;
            long largest = 0;
            foreach (var (a, b) in pairs)
            {
                var child = store.Root.CreateChild();
                child.Union(a, b);
                child.Rebuild();
                var copied = child.Tables!.Statistics.RowsCopied;
                child.Dispose();
                buckets[copied == 0 ? 0 : 64 - System.Numerics.BitOperations.LeadingZeroCount((ulong)copied)]++;
                total += copied;
                largest = Math.Max(largest, copied);
            }

            return (buckets, total, largest);
        }

        /// <summary>
        /// Formats the non-empty buckets as <c>range: count</c>, where the range is <c>0</c>, <c>1</c>, or
        /// <c>lo-hi</c> with both ends included.
        /// </summary>
        /// <param name="buckets">The bucket counts <see cref="Histogram"/> returns.</param>
        /// <returns>The text.</returns>
        internal static string Format(long[] buckets)
        {
            var text = new StringBuilder();
            for (var k = 0; k < buckets.Length; k++)
            {
                if (buckets[k] == 0)
                    continue;

                if (text.Length > 0)
                    text.Append(", ");

                var range = k == 0 ? "0" : k == 1 ? "1" : $"{1L << (k - 1)}-{(1L << k) - 1}";
                text.Append(CultureInfo.InvariantCulture, $"{range}: {buckets[k]}");
            }

            return text.ToString();
        }

        /// <inheritdoc />
        /// <remarks>
        /// The unions one invocation makes.
        /// </remarks>
        long IItemsPerInvocation.ItemsPerInvocation(string benchmarkMethod) => Unions;

    }

}

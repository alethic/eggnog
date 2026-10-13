using System;

using BenchmarkDotNet.Attributes;

namespace Eggnog.Relations.Benchmarks
{

    /// <summary>
    /// <c>find</c> in a child: the root's find (one hop on a frozen, compressed root), a child's layered find (the
    /// root's find, then one probe per non-empty level of the chain), and a child under a frozen chain, whose parent's
    /// levels are flattened into one map (one dense load or probe). Chains of depth 1 and 3 over a frozen root of
    /// 1,000,000 ids with 500,000 unions; each level makes 1,000 unions of its own.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Each invocation finds the same 1,000,000 ids drawn from a fixed seed, through the raw finds
    /// (<see cref="LayeredUnionFind.Find"/>), which is what the engine's rewrites and lookups call, and through
    /// <see cref="Context.Find"/>, which adds the readability checks (and, for a context that is not frozen, the
    /// thread-domain enter). The leaf of the explicit chain is not frozen, so its levels stay explicit; the flattened
    /// case freezes every level above the leaf and creates the leaf afterwards. Every case checks its checksum against
    /// the explicit chain's in the setup, so a wrong find fails the run.
    /// </para>
    /// <para>The class is not sealed because BenchmarkDotNet generates a class that derives from it.</para>
    /// </remarks>
    public class ChildFindBenchmarks : IItemsPerInvocation
    {

        /// <summary>
        /// The number of root ids.
        /// </summary>
        public const int Ids = 1_000_000;

        /// <summary>
        /// The number of finds per invocation.
        /// </summary>
        public const int Finds = 1_000_000;

        /// <summary>
        /// The seed the unions and the ids to find are drawn from.
        /// </summary>
        public const int Seed = 0x5EED;

        Store? store;
        Context? explicitLeaf;
        Context? flattenedLeaf;
        uint[]? toFind;

        /// <summary>
        /// Gets or sets the depth of the leaf child.
        /// </summary>
        [Params(1, 3)]
        public int Depth { get; set; }

        /// <summary>
        /// Builds the frozen root, an explicit chain and a flattened one, and checks that every find agrees.
        /// </summary>
        /// <exception cref="InvalidOperationException">The finds disagree.</exception>
        [GlobalSetup]
        public void GlobalSetup()
        {
            var random = new Random(Seed);
            store = new Store(ParallelHeuristics.Serial);
            var root = store.Root;
            for (var i = 0; i < Ids; i++)
                root.NewId();

            root.Write((random, store.UnionFindTable), static (state, args) =>
            {
                Span<uint> row = stackalloc uint[3];
                for (var i = 0; i < Ids / 2; i++)
                {
                    row[0] = (uint)args.random.Next(Ids);
                    row[1] = (uint)args.random.Next(Ids);
                    row[2] = state.Timestamp;
                    state.StageInsert(args.UnionFindTable, row);
                }

                return 0;
            });
            root.Rebuild();
            root.Freeze();

            // The same unions per level for both chains, so both leaves have the same classes.
            var levels = new (uint, uint)[Depth][];
            for (var level = 0; level < Depth; level++)
            {
                levels[level] = new (uint, uint)[1000];
                for (var i = 0; i < levels[level].Length; i++)
                    levels[level][i] = ((uint)random.Next(Ids), (uint)random.Next(Ids));
            }

            explicitLeaf = Chain(root, levels, freeze: false);
            flattenedLeaf = Chain(root, levels, freeze: true);
            toFind = new uint[Finds];
            for (var i = 0; i < Finds; i++)
                toFind[i] = (uint)random.Next(Ids);

            if (ExplicitChain() != FlattenedChain() || ExplicitChain() != ExplicitChainChecked())
                throw new InvalidOperationException("The finds of the two chains disagree.");
        }

        /// <summary>
        /// The frozen root's find.
        /// </summary>
        /// <returns>A checksum of the canonical ids.</returns>
        [Benchmark(Baseline = true)]
        public long Root() => Sum(store!.Root.UnionFind);

        /// <summary>
        /// The leaf's layered find over an explicit chain.
        /// </summary>
        /// <returns>A checksum of the canonical ids.</returns>
        [Benchmark]
        public long ExplicitChain() => Sum(explicitLeaf!.UnionFind);

        /// <summary>
        /// The leaf's layered find through <see cref="Context.Find"/>: checks and the domain enter included.
        /// </summary>
        /// <returns>A checksum of the canonical ids.</returns>
        [Benchmark]
        public long ExplicitChainChecked()
        {
            var leaf = explicitLeaf!;
            long sum = 0;
            foreach (var id in toFind!)
                sum += leaf.Find(id);

            return sum;
        }

        /// <summary>
        /// The leaf's find under a frozen, flattened chain.
        /// </summary>
        /// <returns>A checksum of the canonical ids.</returns>
        [Benchmark]
        public long FlattenedChain() => Sum(flattenedLeaf!.UnionFind);

        /// <summary>
        /// Builds a chain of <c>levels.Length</c> children under <paramref name="root"/>, each making its level's unions;
        /// with <paramref name="freeze"/>, every level above the leaf is frozen before its child is created.
        /// </summary>
        static Context Chain(Context root, (uint A, uint B)[][] levels, bool freeze)
        {
            var parent = root;
            Context? context = null;
            for (var level = 0; level < levels.Length; level++)
            {
                context = parent.CreateChild();
                context.Write((levels[level], context.Store.UnionFindTable), static (state, args) =>
                {
                    Span<uint> row = stackalloc uint[3];
                    foreach (var (a, b) in args.Item1)
                    {
                        row[0] = a;
                        row[1] = b;
                        row[2] = state.Timestamp;
                        state.StageInsert(args.UnionFindTable, row);
                    }

                    return 0;
                });
                context.Rebuild();
                if (freeze && level + 1 < levels.Length)
                    context.Freeze();

                parent = context;
            }

            return context!;
        }

        /// <summary>
        /// Finds every id of the workload through <paramref name="find"/>.
        /// </summary>
        long Sum(LayeredUnionFind find)
        {
            long sum = 0;
            foreach (var id in toFind!)
                sum += find.Find(id);

            return sum;
        }

        /// <inheritdoc />
        /// <remarks>
        /// The finds of an invocation.
        /// </remarks>
        long IItemsPerInvocation.ItemsPerInvocation(string benchmarkMethod) => Finds;

    }

}

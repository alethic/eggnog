using System;

using BenchmarkDotNet.Attributes;

namespace Eggnog.Relations.Benchmarks
{

    /// <summary>
    /// What one child costs in containers of classes, as its root's containers grow: over a frozen root of 20,000,
    /// 200,000 and 2,000,000 vectors (<see cref="ContainerRootWorkload"/>), a child's container pass, and a child's
    /// catch-up with the containers its writable parent claims and rebuilds after it exists. A child's work is
    /// proportional to the containers it touches, never to the root's count, so time and allocated bytes per invocation
    /// must stay flat within 1.5× across the three sizes; the setup checks what one child holds, and what the
    /// catch-up's child reads of its parent's container log.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The pass (<see cref="ChildPass"/>): a child of the root unions <c>x(2j)</c> and <c>x(2j+1)</c> for 100 pairs,
    /// so each loser <c>x(2j+1)</c> is held by two vectors, which the child's rebuild claims through the root's element
    /// index and rebuilds in its layer: 200 claims, then the child is disposed.
    /// </para>
    /// <para>
    /// The catch-up (<see cref="ChildCatchUp"/>), a case split's shape: a writable parent of the root and its child;
    /// the child unions <c>x(4j)</c> and <c>x(4j+1)</c>, claiming <c>v(4j)</c> and <c>v(4j+1)</c>; then the parent
    /// unions <c>x(4j+1)</c> and <c>x(4j+2)</c>, claiming <c>v(4j+1)</c> and <c>v(4j+2)</c> in its own layer, which
    /// its container log records; then the child rebuilds. Its pre-loop scan reads the parent's 400 entries and keeps
    /// 200, which the setup checks; its catch-up leaves the parent's copy of <c>v(4j+1)</c>, which the child's copy
    /// stands for, and claims <c>v(4j+2)</c>, which holds the child's loser; and its pass merges <c>v(4j+1)</c> into
    /// <c>v(4j)</c>, whose copies become equal. Both are disposed, so the root never changes.
    /// </para>
    /// <para>
    /// Each invocation creates and disposes its own contexts, so BenchmarkDotNet chooses the invocation count; the
    /// memory diagnoser's allocated bytes per operation are the per-child bytes. The class is not sealed because
    /// BenchmarkDotNet generates a class that derives from it.
    /// </para>
    /// </remarks>
    public class PerChildContainerCostBenchmarks : IItemsPerInvocation
    {

        /// <summary>
        /// The unions one child makes, and one parent in the catch-up.
        /// </summary>
        public const int Unions = 100;

        ContainerRootWorkload? workload;

        /// <summary>
        /// Gets or sets the number of the root's vectors, and of its classes.
        /// </summary>
        [Params(20_000, 200_000, 2_000_000)]
        public int RootContainers { get; set; }

        /// <summary>
        /// Builds the frozen root, and checks that one child of each workload holds exactly what it should, and that
        /// the catch-up's child reads and keeps the entries of its parent's container log it should.
        /// </summary>
        /// <exception cref="InvalidOperationException">A child holds or shadows other containers than expected, or the
        /// catch-up's child reads or keeps other entries of its parent's log.</exception>
        [GlobalSetup]
        public void GlobalSetup()
        {
            workload = new ContainerRootWorkload(RootContainers);
            var (held, shadowed, _) = Pass(workload);
            if (held != 2 * Unions || shadowed != 2 * Unions)
                throw new InvalidOperationException($"A child's pass holds {held} vectors and shadows {shadowed}; expected {2 * Unions} and {2 * Unions}.");

            var (caughtUpHeld, caughtUpShadowed, merged) = CatchUp(workload, checkScan: true);
            if (caughtUpHeld != 2 * Unions || caughtUpShadowed != 3 * Unions || merged != Unions)
                throw new InvalidOperationException($"A child's catch-up holds {caughtUpHeld} vectors, shadows {caughtUpShadowed} and merged {merged}; expected {2 * Unions}, {3 * Unions} and {Unions}.");
        }

        /// <summary>
        /// One child: creation, 100 unions, a rebuild that claims and rebuilds 200 vectors, disposal.
        /// </summary>
        /// <returns>The vectors the child's layer holds.</returns>
        [Benchmark]
        public long ChildPass() => Pass(workload!).Held;

        /// <summary>
        /// One writable parent and its child: creation, 100 unions in each, the parent's rebuild, the child's rebuild
        /// with its catch-up, disposal.
        /// </summary>
        /// <returns>The vectors the child's layer holds.</returns>
        [Benchmark]
        public long ChildCatchUp() => CatchUp(workload!).Held;

        /// <summary>
        /// Runs the pass workload once over <paramref name="root"/>: what the child's layer holds and shadows.
        /// </summary>
        static (long Held, long Shadowed, long Merged) Pass(ContainerRootWorkload root)
        {
            var x = root.Classes;
            var child = root.Store.Root.CreateChild();
            for (var j = 0; j < Unions; j++)
                child.Union(x[2 * j], x[2 * j + 1]);

            child.Rebuild();
            var layer = child.ContainerLayers!.LayerAt(root.Env.TypeId)!;
            var result = ((long)layer.OwnBase.Count, (long)layer.Shadowed.Count, 0L);
            child.Dispose();
            return result;
        }

        /// <summary>
        /// Runs the catch-up workload once over <paramref name="root"/>: what the child's layer holds and shadows, and
        /// how many of its copies of <c>v(4j+1)</c> merged into <c>v(4j)</c>. With <paramref name="checkScan"/>, for
        /// the setup only, the child's pre-loop scan runs on its own before the child's rebuild, whose own scan then
        /// finds nothing new, and its counts are checked.
        /// </summary>
        /// <exception cref="InvalidOperationException">With <paramref name="checkScan"/>, the scan reads or keeps
        /// other entries of the parent's log than expected.</exception>
        static (long Held, long Shadowed, long Merged) CatchUp(ContainerRootWorkload root, bool checkScan = false)
        {
            var x = root.Classes;
            var v = root.Vectors;
            var parent = root.Store.Root.CreateChild();
            var child = parent.CreateChild();
            for (var j = 0; j < Unions; j++)
                child.Union(x[4 * j], x[4 * j + 1]);

            child.Rebuild();
            for (var j = 0; j < Unions; j++)
                parent.Union(x[4 * j + 1], x[4 * j + 2]);

            parent.Rebuild();
            var layers = child.ContainerLayers!;
            if (checkScan)
            {
                layers.ScanAdds();
                var read = parent.ContainerLayers!.Log.AddedCount;
                if (read != 4 * Unions || layers.PendingCount != 2 * Unions)
                    throw new InvalidOperationException($"The child's pre-loop scan read {read} entries of its parent's container log and kept {layers.PendingCount}; expected {4 * Unions} and {2 * Unions}.");
            }

            child.Rebuild();
            var layer = layers.LayerAt(root.Env.TypeId)!;
            var merged = 0L;
            for (var j = 0; j < Unions; j++)
                if (child.Find(v[4 * j + 1]) == v[4 * j])
                    merged++;

            var result = ((long)layer.OwnBase.Count, (long)layer.Shadowed.Count, merged);
            child.Dispose();
            parent.Dispose();
            return result;
        }

        /// <inheritdoc />
        /// <remarks>
        /// The unions one child makes.
        /// </remarks>
        long IItemsPerInvocation.ItemsPerInvocation(string benchmarkMethod) => benchmarkMethod is nameof(ChildPass) or nameof(ChildCatchUp) ? Unions : 0;

    }

}

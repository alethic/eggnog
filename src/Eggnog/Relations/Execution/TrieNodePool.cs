using System;

namespace Eggnog.Relations
{

    /// <summary>
    /// A free list of <see cref="TrieNode"/>s owned by one join task: frames create a node for every sparse refinement
    /// and release it after their recursion, so recycling them keeps the join from allocating per candidate (egglog
    /// allocates an <c>Arc&lt;TrieNode&gt;</c> each time). It also owns the task's <see cref="SubsetArrayPool"/>
    /// (<see cref="Layers"/>), the per-layer arrays of the multi-layer row sets of a child's run (colored-contexts spec
    /// §9.2, §9.5).
    /// </summary>
    /// <remarks>
    /// Released nodes keep their cache arrays, small child maps and disposed per-column indexes (refilled in place on
    /// the next build) for reuse. At most <see cref="MaxPooled"/> nodes are kept. The serial join's pool outlives its
    /// run: the database keeps the join for the next rule set. Not thread-safe; each task has its own pool, and a node
    /// released by another task's pool simply lands in that pool (with its per-layer array, which holds no state).
    /// </remarks>
    internal sealed class TrieNodePool
    {

        /// <summary>
        /// The most nodes the pool keeps.
        /// </summary>
        public const int MaxPooled = 4096;

        readonly bool concurrent;
        TrieNode[] free = new TrieNode[16];
        int count;

        /// <summary>
        /// Initializes an empty pool for a run whose tables have one layer: a root's.
        /// </summary>
        /// <param name="concurrent">Whether the nodes it hands out may be shared by parallel tasks.</param>
        public TrieNodePool(bool concurrent) :
            this(concurrent, 1)
        {

        }

        /// <summary>
        /// Initializes an empty pool for a run whose tables' views have <paramref name="layerCount"/> layers.
        /// </summary>
        /// <param name="concurrent">Whether the nodes it hands out may be shared by parallel tasks.</param>
        /// <param name="layerCount">The running context's depth plus one, in <c>[1, <see cref="LayeredSubset.MaxLayers"/>]</c>.
        /// A root's run (one layer) never creates a multi-layer row set and gets no array pool.</param>
        public TrieNodePool(bool concurrent, int layerCount)
        {
            if (layerCount < 1 || layerCount > LayeredSubset.MaxLayers)
                throw new ArgumentOutOfRangeException(nameof(layerCount), layerCount, $"A layer count lies in [1, {LayeredSubset.MaxLayers}].");

            this.concurrent = concurrent;
            Layers = layerCount > 1 ? new SubsetArrayPool(layerCount) : null;
        }

        /// <summary>
        /// Gets whether the nodes it hands out may be shared by parallel tasks.
        /// </summary>
        public bool IsConcurrent => concurrent;

        /// <summary>
        /// Gets the per-layer arrays of the run's multi-layer row sets, or <c>null</c> for a run with one layer.
        /// </summary>
        public SubsetArrayPool? Layers { get; }

        /// <summary>
        /// Takes a node and gives it <paramref name="rows"/>, in layer 0.
        /// </summary>
        /// <param name="rows">The rows; owned by the node from now on.</param>
        /// <returns>The node, with no caches.</returns>
        public TrieNode Rent(Subset rows) => Rent(0, rows);

        /// <summary>
        /// Takes a node and gives it <paramref name="rows"/> of layer <paramref name="layer"/>.
        /// </summary>
        /// <param name="layer">The layer of the rows.</param>
        /// <param name="rows">The rows; owned by the node from now on.</param>
        /// <returns>The node, with no caches.</returns>
        public TrieNode Rent(int layer, Subset rows)
        {
            var node = Take();
            node.Initialize(layer, rows, concurrent);
            return node;
        }

        /// <summary>
        /// Takes a node and gives it <paramref name="rows"/>.
        /// </summary>
        /// <param name="rows">The rows, of any number of layers; owned by the node from now on.</param>
        /// <returns>The node, with no caches.</returns>
        public TrieNode Rent(LayeredSubset rows)
        {
            var node = Take();
            node.Initialize(rows, concurrent);
            return node;
        }

        /// <summary>
        /// Takes back a node whose rows and caches were released. Called by <see cref="TrieNode.Release"/>.
        /// </summary>
        /// <param name="node">The node.</param>
        public void Return(TrieNode node)
        {
            if (count == MaxPooled)
                return;

            if (count == free.Length)
                Array.Resize(ref free, free.Length * 2);

            free[count++] = node;
        }

        /// <summary>
        /// Takes a pooled node, or a new one when the pool is empty.
        /// </summary>
        TrieNode Take()
        {
            if (count == 0)
                return new TrieNode();

            var node = free[--count];
            free[count] = null!;
            return node;
        }

    }

}

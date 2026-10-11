using System;

namespace Eggnog.Relations
{

    /// <summary>
    /// A free list of <see cref="TrieNode"/>s owned by one join task: frames create a node for every sparse refinement
    /// and release it after their recursion, so recycling them keeps the join from allocating per candidate (egglog
    /// allocates an <c>Arc&lt;TrieNode&gt;</c> each time).
    /// </summary>
    /// <remarks>
    /// Released nodes keep their cache arrays, small child maps and disposed per-column indexes (refilled in place on
    /// the next build) for reuse. At most <see cref="MaxPooled"/> nodes are kept. The serial join's pool outlives its
    /// run: the database keeps the join for the next rule set. Not thread-safe; each task has its own pool, and a node
    /// released by another task's pool simply lands in that pool.
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
        /// Initializes an empty pool.
        /// </summary>
        /// <param name="concurrent">Whether the nodes it hands out may be shared by parallel tasks.</param>
        public TrieNodePool(bool concurrent)
        {
            this.concurrent = concurrent;
        }

        /// <summary>
        /// Gets whether the nodes the pool hands out may be shared by parallel tasks.
        /// </summary>
        public bool IsConcurrent => concurrent;

        /// <summary>
        /// Takes a node and gives it <paramref name="rows"/>.
        /// </summary>
        /// <param name="rows">The rows; owned by the node from now on.</param>
        /// <returns>The node, with no caches.</returns>
        public TrieNode Rent(Subset rows)
        {
            TrieNode node;
            if (count > 0)
            {
                node = free[--count];
                free[count] = null!;
            }
            else
            {
                node = new TrieNode();
            }

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

    }

}

namespace Eggnog.Relations
{

    /// <summary>
    /// The state of a <see cref="TrieNode"/> that the nodes of a parallel root run never need: the per-layer rows of a
    /// multi-layer node (colored-contexts spec §9.5) and the spare indexes a recycled node refills. Kept out of the node so
    /// that a node whose rows lie in one layer, which is every node of a root run, stays as small as it was before layered
    /// execution (§1 item 8, §14.1): a parallel run allocates a fresh node per frame at positions 0 and 1.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Created on a node's first multi-layer rows or first kept spare, and kept with the node through the pool for every
    /// later use, so a warm serial run allocates none. A concurrent node with multi-layer rows (a child's parallel run)
    /// allocates one with the node.
    /// </para>
    /// <para>
    /// Owned by its node and written only while no other thread can reach the node (when the node is given rows, reset or
    /// released); a spare is taken with an interlocked exchange when the node is concurrent.
    /// </para>
    /// </remarks>
    internal sealed class TrieNodeSideState
    {

        /// <summary>
        /// The rows of a multi-layer node; <see cref="LayeredSubset.Empty"/> while the node's rows lie in one layer. A
        /// field, so the subset is mutated in place.
        /// </summary>
        public LayeredSubset Rows;

        /// <summary>
        /// The disposed indexes the node keeps for reuse, by index slot, or <c>null</c> before the first is kept.
        /// </summary>
        public ColumnIndex?[]? Spares;

    }

}

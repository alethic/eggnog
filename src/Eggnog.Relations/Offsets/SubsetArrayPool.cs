using System;
using System.Diagnostics;

namespace Eggnog.Relations
{

    /// <summary>
    /// A stack of <see cref="Subset"/> arrays of one length, the per-layer storage of multi-layer
    /// <see cref="LayeredSubset"/>s (colored-contexts spec §9.2): one array per layered trie node or probe result in a
    /// child's run, reused instead of allocated. The layered executor owns one per run, sized to the running context's
    /// layer count (its depth plus one), which bounds the layers of every table it reads (M10.2: owned by
    /// <c>TrieNodePool</c>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Arrays come back cleared: every entry <see cref="Subset.Empty"/>. A returned array must own no buffers (its owner
    /// disposes the subsets first, as <see cref="LayeredSubset.Dispose"/> does); the pool only clears the entries. An
    /// array of another length is dropped rather than kept, so a <see cref="LayeredSubset"/> built on a fresh array of
    /// any length may be disposed into any pool. At most <see cref="MaxRetained"/> arrays are kept.
    /// </para>
    /// <para>
    /// Not thread-safe: one run task, one pool. A root run never creates multi-layer subsets and never touches a pool.
    /// </para>
    /// </remarks>
    internal sealed class SubsetArrayPool
    {

        /// <summary>
        /// The most arrays the pool keeps; more are dropped on return.
        /// </summary>
        public const int MaxRetained = 1024;

        readonly int layerCount;
        Subset[][] free = new Subset[8][];
        int count;

        /// <summary>
        /// Initializes an empty pool of arrays of <paramref name="layerCount"/> entries.
        /// </summary>
        /// <param name="layerCount">The array length: a layer count, in <c>[1, <see cref="LayeredSubset.MaxLayers"/>]</c>.</param>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="layerCount"/> is out of range.</exception>
        public SubsetArrayPool(int layerCount)
        {
            if (layerCount < 1 || layerCount > LayeredSubset.MaxLayers)
                throw new ArgumentOutOfRangeException(nameof(layerCount), layerCount, $"A layer count lies in [1, {LayeredSubset.MaxLayers}].");

            this.layerCount = layerCount;
        }

        /// <summary>
        /// Gets the length of the arrays the pool hands out.
        /// </summary>
        public int LayerCount => layerCount;

        /// <summary>
        /// Gets the number of arrays the pool holds now.
        /// </summary>
        public int Count => count;

        /// <summary>
        /// Takes an array of <see cref="LayerCount"/> empty entries: a pooled one, or a new one when the pool is empty.
        /// O(1).
        /// </summary>
        /// <returns>The array, which the caller owns until it returns it.</returns>
        public Subset[] Rent()
        {
            if (count == 0)
                return new Subset[layerCount];

            var array = free[--count];
            free[count] = null!;
            return array;
        }

        /// <summary>
        /// Gives an array back: cleared and kept when it has <see cref="LayerCount"/> entries and the pool is not full,
        /// dropped otherwise. Its subsets must already be disposed. O(<see cref="LayerCount"/>).
        /// </summary>
        /// <param name="array">The array.</param>
        public void Return(Subset[] array)
        {
            ArgumentNullException.ThrowIfNull(array);
            if (array.Length != layerCount || count == MaxRetained)
                return;

            AssertOwnsNothing(array);
            Array.Clear(array);
            if (count == free.Length)
                Array.Resize(ref free, Math.Min(free.Length * 2, MaxRetained));

            free[count++] = array;
        }

        /// <summary>
        /// Debug builds: checks that no entry still owns a pooled buffer, which clearing would leak.
        /// </summary>
        [Conditional("DEBUG")]
        static void AssertOwnsNothing(Subset[] array)
        {
            foreach (var subset in array)
                Debug.Assert(subset.IsDense, "a subset array went back to its pool with a sparse subset it still owns");
        }

    }

}

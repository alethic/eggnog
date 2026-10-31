using System.Collections.Generic;

using Xunit;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// Reads the layers of layered row sets and views in the forms tests compare: a list of layers, or a mask of the
    /// layers of a shallow chain.
    /// </summary>
    static class LayerTestUtil
    {

        /// <summary>
        /// Gets the layers that hold rows of <paramref name="subset"/>, in the order <see cref="LayeredSubset.GetLayers"/>
        /// gives them.
        /// </summary>
        public static int[] Layers(in LayeredSubset subset) => Collect(subset.GetLayers());

        /// <summary>
        /// Gets the layers that hold rows of <paramref name="node"/>, in the order <see cref="TrieNode.GetLayers"/> gives
        /// them.
        /// </summary>
        public static int[] Layers(TrieNode node) => Collect(node.GetLayers());

        /// <summary>
        /// Gets the layers that hold rows of <paramref name="subset"/> as a mask, bit <c>l</c> for layer <c>l</c>; every
        /// layer must be below 64.
        /// </summary>
        public static ulong Mask(in LayeredSubset subset) => ToMask(Layers(subset));

        /// <summary>
        /// Gets the layers that hold rows of <paramref name="node"/> as a mask; every layer must be below 64.
        /// </summary>
        public static ulong Mask(TrieNode node) => ToMask(Layers(node));

        /// <summary>
        /// Gets the present layers of <paramref name="view"/> as a mask, checking <see cref="TableView.PresentCount"/>
        /// and <see cref="TableView.FirstPresent"/> against them; every layer must be below 64.
        /// </summary>
        public static ulong PresentMask(TableView view)
        {
            var present = new List<int>();
            for (var j = 0; j < view.Layers.Length; j++)
                if (view.Layers[j] is not null)
                    present.Add(j);

            Assert.Equal(present.Count, view.PresentCount);
            Assert.Equal(present.Count == 0 ? -1 : present[0], view.FirstPresent);
            return ToMask(present.ToArray());
        }

        static int[] Collect(LayerEnumerator layers)
        {
            var list = new List<int>();
            foreach (var layer in layers)
                list.Add(layer);

            return list.ToArray();
        }

        static ulong ToMask(IReadOnlyList<int> layers)
        {
            var mask = 0UL;
            foreach (var layer in layers)
            {
                Assert.InRange(layer, 0, 63);
                mask |= 1UL << layer;
            }

            return mask;
        }

    }

}

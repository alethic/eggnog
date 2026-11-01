using System.Numerics;
using System.Runtime.CompilerServices;

namespace Eggnog.Relations
{

    /// <summary>
    /// Enumerates the layers that hold rows of a <see cref="LayeredSubset"/> or a <see cref="TrieNode"/>, in ascending
    /// order: what the layered executor walks to read a row set layer by layer. Returned by their <c>GetLayers</c>
    /// methods, so <c>foreach</c> over the layers allocates nothing.
    /// </summary>
    /// <remarks>
    /// Layers below <see cref="LayeredSubset.MaskedLayers"/> come from a 64-bit mask, one trailing-zero count each; a
    /// single layer below that is one bit, so a root's one layer costs what a mask loop costs. Layers from
    /// <see cref="LayeredSubset.MaskedLayers"/> on, which only a chain deeper than 63 has, are found by testing the
    /// per-layer entries from there to the end of the array (or are the one layer of a single-layer row set). The
    /// enumerator borrows the array: it is valid only while the owner does not change or return it, except that the
    /// caller may empty or dispose the entry of the layer it was just given.
    /// </remarks>
    internal struct LayerEnumerator
    {

        readonly Subset[]? layers;
        readonly int end;
        ulong remaining;
        int next;
        int current;

        /// <summary>
        /// Initializes an enumerator over the bits of <paramref name="mask"/>, then over the layers in
        /// <c>[<paramref name="next"/>, <paramref name="end"/>)</c>: each one whose entry of <paramref name="layers"/> is
        /// nonempty, or each one when <paramref name="layers"/> is <c>null</c>.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        LayerEnumerator(ulong mask, Subset[]? layers, int next, int end)
        {
            this.layers = layers;
            this.end = end;
            remaining = mask;
            this.next = next;
            current = -1;
        }

        /// <summary>
        /// Gets an enumerator over no layer.
        /// </summary>
        public static LayerEnumerator Empty
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => default;
        }

        /// <summary>
        /// Gets an enumerator over the one layer <paramref name="layer"/>.
        /// </summary>
        /// <param name="layer">The layer, not negative.</param>
        /// <returns>The enumerator.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static LayerEnumerator Single(int layer) =>
            layer < LayeredSubset.MaskedLayers ? new(1UL << layer, null, 0, 0) : new(0, null, layer, layer + 1);

        /// <summary>
        /// Gets an enumerator over the layers of a multi-layer row set: the bits of <paramref name="mask"/>, then the
        /// nonempty entries of <paramref name="layers"/> from <see cref="LayeredSubset.MaskedLayers"/> on.
        /// </summary>
        /// <param name="mask">The nonempty layers below <see cref="LayeredSubset.MaskedLayers"/>.</param>
        /// <param name="layers">The per-layer rows.</param>
        /// <returns>The enumerator.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static LayerEnumerator Multi(ulong mask, Subset[] layers) => new(mask, layers, LayeredSubset.MaskedLayers, layers.Length);

        /// <summary>
        /// Gets the current layer.
        /// </summary>
        public readonly int Current
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => current;
        }

        /// <summary>
        /// Gets the enumerator itself, so <c>foreach</c> works over it.
        /// </summary>
        /// <returns>A copy of the enumerator.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public readonly LayerEnumerator GetEnumerator() => this;

        /// <summary>
        /// Advances to the next layer that holds rows.
        /// </summary>
        /// <returns><c>false</c> when there is none.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool MoveNext()
        {
            if (remaining != 0)
            {
                current = BitOperations.TrailingZeroCount(remaining);
                remaining &= remaining - 1;
                return true;
            }

            return next < end && MoveNextWide();
        }

        /// <summary>
        /// Advances over the layers from <see cref="LayeredSubset.MaskedLayers"/> on.
        /// </summary>
        bool MoveNextWide()
        {
            while (next < end)
            {
                var layer = next++;
                if (layers is null || layers[layer].IsEmpty == false)
                {
                    current = layer;
                    return true;
                }
            }

            return false;
        }

    }

}

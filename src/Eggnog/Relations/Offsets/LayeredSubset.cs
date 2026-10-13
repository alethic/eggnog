using System;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Text;

namespace Eggnog.Relations
{

    /// <summary>
    /// An owned set of rows of one table as a context sees it: a <see cref="Subset"/> per layer of the table's view
    /// (colored-contexts spec §9.2), the row set of every trie node, header and probe result of a layered run. Rows of
    /// different layers are different rows, even with equal ids.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Representation. While the rows lie in at most one layer, the subset is <em>single</em>: one <see cref="Subset"/>
    /// and its layer, which is what every root subset is (layer 0), so the root path is today's subset behind one null
    /// test. Once two or more layers are nonempty it is <em>multi-layer</em>: an array of per-layer subsets indexed by
    /// layer, with a mask of the nonempty ones; the entries outside the mask are <see cref="Subset.Empty"/>. Every
    /// operation that can empty layers collapses back: a multi-layer subset always has at least two nonempty layers.
    /// <c>default</c> is <see cref="Empty"/>.
    /// </para>
    /// <para>
    /// Ownership. Move-only, as <see cref="Subset"/> is: a struct copy aliases the per-layer buffers (and the array), so
    /// keep using only one copy (<see cref="Take"/> moves explicitly, <see cref="Clone"/> copies), and release it once
    /// with <see cref="Dispose"/>. The per-layer arrays come from a <see cref="SubsetArrayPool"/> when one is given, and
    /// go back to it when a subset collapses or is disposed; without a pool they are allocated and dropped. The mutators
    /// work in place, so hold the struct in a field, local or array element, never behind a property or in a readonly
    /// field.
    /// </para>
    /// <para>
    /// At most <see cref="MaxLayers"/> layers, because the layer mask is a <see cref="uint"/> (X36). Not thread-safe for
    /// mutation; any number of threads may read one nobody mutates.
    /// </para>
    /// </remarks>
    internal struct LayeredSubset
    {

        /// <summary>
        /// The most layers a subset can span: the deepest context chain (X36).
        /// </summary>
        public const int MaxLayers = 32;

        Subset single;
        int singleLayer;
        Subset[]? layers;
        uint mask;

        /// <summary>
        /// Initializes a single-layer subset.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        LayeredSubset(int layer, Subset rows)
        {
            single = rows;
            singleLayer = layer;
            layers = null;
            mask = 0;
        }

        /// <summary>
        /// Initializes a multi-layer subset over <paramref name="layers"/>, whose nonempty entries are
        /// <paramref name="mask"/>; the caller collapses it if fewer than two are.
        /// </summary>
        LayeredSubset(Subset[] layers, uint mask)
        {
            single = default;
            singleLayer = 0;
            this.layers = layers;
            this.mask = mask;
        }

        /// <summary>
        /// Gets the empty subset: single, layer 0, <see cref="Subset.Empty"/>.
        /// </summary>
        public static LayeredSubset Empty
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => default;
        }

        /// <summary>
        /// Gets whether the rows lie in two or more layers (the per-layer array form). One null test.
        /// </summary>
        public readonly bool IsMultiLayer
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => layers is not null;
        }

        /// <summary>
        /// Gets the number of rows over every layer, counting duplicates: <see cref="Subset.Size"/> of a single-layer
        /// subset, else the sum over the nonempty layers. O(1), or O(nonempty layers).
        /// </summary>
        public readonly int Size
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => layers is null ? single.Size : MultiSize();
        }

        /// <summary>
        /// Gets whether no layer holds a row. O(1): a multi-layer subset has rows in at least two layers.
        /// </summary>
        public readonly bool IsEmpty
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => layers is null && single.IsEmpty;
        }

        /// <summary>
        /// Gets the layers that hold rows: bit <c>l</c> is set when layer <c>l</c> is nonempty. A single-layer subset has
        /// its layer's bit, or no bit when empty.
        /// </summary>
        public readonly uint LayerMask
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => layers is null ? (single.IsEmpty ? 0u : 1u << singleLayer) : mask;
        }

        /// <summary>
        /// Gets the layer of a single-layer subset (0 for <see cref="Empty"/>), or −1 for a multi-layer one.
        /// </summary>
        public readonly int SingleLayer
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => layers is null ? singleLayer : -1;
        }

        /// <summary>
        /// Gets the rows of a single-layer subset, borrowed: valid until the subset is next mutated or disposed. The root
        /// path reads its subsets through this.
        /// </summary>
        /// <exception cref="InvalidOperationException">The subset is multi-layer.</exception>
        public readonly SubsetRef SingleRows
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get
            {
                if (layers is not null)
                    ThrowMultiLayer();

                return single.AsRef();
            }
        }

        /// <summary>
        /// Creates a single-layer subset holding <paramref name="rows"/> of layer <paramref name="layer"/>. O(1).
        /// </summary>
        /// <param name="layer">The layer, in <c>[0, <see cref="MaxLayers"/>)</c>.</param>
        /// <param name="rows">The rows, whose ownership moves to the result.</param>
        /// <returns>The subset.</returns>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="layer"/> is out of range.</exception>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static LayeredSubset Single(int layer, Subset rows)
        {
            if ((uint)layer >= MaxLayers)
                ThrowLayerOutOfRange(layer);

            return new LayeredSubset(layer, rows);
        }

        /// <summary>
        /// Creates a subset from per-layer rows: entry <c>l</c> of <paramref name="layers"/> is the rows of layer
        /// <c>l</c>. Collapses to a single-layer subset (or <see cref="Empty"/>) when fewer than two entries are nonempty,
        /// returning the array to <paramref name="pool"/>; empty entries are disposed. O(array length).
        /// </summary>
        /// <param name="layers">The per-layer rows, at most <see cref="MaxLayers"/>; the array and every entry move to the
        /// result (the array to <paramref name="pool"/> when it collapses).</param>
        /// <param name="pool">The pool the array returns to once the subset no longer needs it, or <c>null</c>.</param>
        /// <returns>The subset.</returns>
        /// <exception cref="ArgumentException"><paramref name="layers"/> has more than <see cref="MaxLayers"/> entries.</exception>
        public static LayeredSubset FromLayers(Subset[] layers, SubsetArrayPool? pool)
        {
            ArgumentNullException.ThrowIfNull(layers);
            if (layers.Length > MaxLayers)
                throw new ArgumentException($"A layered subset spans at most {MaxLayers} layers.", nameof(layers));

            var nonempty = 0u;
            for (var l = 0; l < layers.Length; l++)
            {
                if (layers[l].IsEmpty)
                    layers[l].Dispose();
                else
                    nonempty |= 1u << l;
            }

            var result = new LayeredSubset(layers, nonempty);
            result.Collapse(pool);
            return result;
        }

        /// <summary>
        /// Borrows the rows of layer <paramref name="layer"/>: empty when the layer holds none. Valid until the subset is
        /// next mutated or disposed. O(1).
        /// </summary>
        /// <param name="layer">The layer, in <c>[0, <see cref="MaxLayers"/>)</c>.</param>
        /// <returns>The layer's rows.</returns>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="layer"/> is out of range.</exception>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public readonly SubsetRef GetLayer(int layer)
        {
            if ((uint)layer >= MaxLayers)
                ThrowLayerOutOfRange(layer);

            if (layers is null)
                return layer == singleLayer ? single.AsRef() : default;

            return (mask & (1u << layer)) != 0 ? layers[layer].AsRef() : default;
        }

        /// <summary>
        /// Removes every row not in <paramref name="other"/>, layer by layer (a row of layer <c>l</c> survives only if
        /// <paramref name="other"/> holds it in layer <c>l</c>). Two single-layer subsets of one layer intersect with
        /// <see cref="Subset.Intersect"/>, the root path; otherwise each layer of this subset is intersected with the
        /// same layer of <paramref name="other"/> or emptied, and the result collapses. Duplicates follow
        /// <see cref="Subset.Intersect"/>. O(the per-layer intersections).
        /// </summary>
        /// <param name="other">The subset to intersect with; not a part of this one.</param>
        /// <param name="pool">The pool a per-layer array returns to when the result collapses, or <c>null</c>.</param>
        public void Intersect(in LayeredSubset other, SubsetArrayPool? pool)
        {
            if (layers is null)
            {
                if (other.layers is null)
                {
                    if (singleLayer == other.singleLayer)
                        single.Intersect(other.single.AsRef());
                    else
                        single.Dispose();

                    return;
                }

                if ((other.mask & (1u << singleLayer)) != 0)
                    single.Intersect(other.layers[singleLayer].AsRef());
                else
                    single.Dispose();

                return;
            }

            if (other.layers is null)
            {
                // Only the other side's layer can survive: keep it, release the rest.
                var layer = other.singleLayer;
                var kept = Subset.Empty;
                if ((mask & (1u << layer)) != 0)
                {
                    kept = layers[layer].Take();
                    kept.Intersect(other.single.AsRef());
                }

                ReleaseLayers(pool);
                single = kept;
                singleLayer = layer;
                return;
            }

            var remaining = mask;
            var survivors = 0u;
            while (remaining != 0)
            {
                var l = BitOperations.TrailingZeroCount(remaining);
                var bit = 1u << l;
                remaining &= remaining - 1;
                if ((other.mask & bit) != 0)
                {
                    layers[l].Intersect(other.layers[l].AsRef());
                    if (layers[l].IsEmpty)
                        layers[l].Dispose();
                    else
                        survivors |= bit;
                }
                else
                {
                    layers[l].Dispose();
                }
            }

            mask = survivors;
            Collapse(pool);
        }

        /// <summary>
        /// Creates an independent copy: free for a dense single-layer subset; otherwise each nonempty layer is copied
        /// (<see cref="Subset.Clone"/>), into an array from <paramref name="pool"/> when its arrays are long enough.
        /// </summary>
        /// <param name="pool">The pool to take a per-layer array from, or <c>null</c>.</param>
        /// <returns>The copy, which the caller must <see cref="Dispose"/> separately.</returns>
        public readonly LayeredSubset Clone(SubsetArrayPool? pool)
        {
            if (layers is null)
                return new LayeredSubset(singleLayer, single.Clone());

            var copy = RentArray(pool, 32 - BitOperations.LeadingZeroCount(mask));
            var remaining = mask;
            while (remaining != 0)
            {
                var l = BitOperations.TrailingZeroCount(remaining);
                remaining &= remaining - 1;
                copy[l] = layers[l].Clone();
            }

            return new LayeredSubset(copy, mask);
        }

        /// <summary>
        /// Enumerates the rows of a single-layer subset (every root subset, so root code reads a header or node as it read
        /// a plain subset). A multi-layer subset is read per layer (<see cref="GetLayer"/>), since its row ids repeat
        /// across layers.
        /// </summary>
        /// <returns>The enumerator.</returns>
        /// <exception cref="InvalidOperationException">The subset is multi-layer.</exception>
        public readonly OffsetEnumerator GetEnumerator() => SingleRows.GetEnumerator();

        /// <summary>
        /// Moves the rows of a single-layer subset out as a plain <see cref="Subset"/>, leaving <see cref="Empty"/> behind:
        /// how a layered refinement that ended up in one layer becomes an ordinary frame refinement without a copy. O(1).
        /// </summary>
        /// <param name="layer">The layer the rows lie in.</param>
        /// <returns>The rows, now owned by the caller.</returns>
        /// <exception cref="InvalidOperationException">The subset is multi-layer.</exception>
        public Subset TakeSingle(out int layer)
        {
            if (layers is not null)
                ThrowMultiLayer();

            layer = singleLayer;
            var taken = single.Take();
            this = default;
            return taken;
        }

        /// <summary>
        /// Moves the subset out, leaving <see cref="Empty"/> behind. O(1).
        /// </summary>
        /// <returns>The subset, now owned by the caller.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public LayeredSubset Take()
        {
            var taken = this;
            this = default;
            return taken;
        }

        /// <summary>
        /// Releases every per-layer buffer, returns the per-layer array to <paramref name="pool"/>, and leaves
        /// <see cref="Empty"/> behind. Calling it again on the same variable is harmless; calling it on a struct copy of
        /// a disposed subset releases its buffers twice.
        /// </summary>
        /// <param name="pool">The pool the array returns to, or <c>null</c> to drop it.</param>
        public void Dispose(SubsetArrayPool? pool)
        {
            if (layers is null)
                single.Dispose();
            else
                ReleaseLayers(pool);

            this = default;
        }

        /// <inheritdoc />
        public readonly override string ToString()
        {
            if (layers is null)
                return $"L{singleLayer}:{single}";

            var text = new StringBuilder("{");
            var remaining = mask;
            while (remaining != 0)
            {
                var l = BitOperations.TrailingZeroCount(remaining);
                remaining &= remaining - 1;
                if (text.Length > 1)
                    text.Append(", ");

                text.Append('L').Append(l).Append(':').Append(layers[l].ToString());
            }

            return text.Append('}').ToString();
        }

        /// <summary>
        /// Gets an array of at least <paramref name="length"/> entries: from <paramref name="pool"/> when its arrays are
        /// long enough, else a new one.
        /// </summary>
        static Subset[] RentArray(SubsetArrayPool? pool, int length) => pool is not null && pool.LayerCount >= length ? pool.Rent() : new Subset[length];

        /// <summary>
        /// Sums the sizes of the nonempty layers of a multi-layer subset.
        /// </summary>
        readonly int MultiSize()
        {
            var total = 0;
            var remaining = mask;
            while (remaining != 0)
            {
                var l = BitOperations.TrailingZeroCount(remaining);
                remaining &= remaining - 1;
                total += layers![l].Size;
            }

            return total;
        }

        /// <summary>
        /// Turns a multi-layer subset with fewer than two nonempty layers into a single-layer one (or
        /// <see cref="Empty"/>), returning its array to <paramref name="pool"/>.
        /// </summary>
        void Collapse(SubsetArrayPool? pool)
        {
            Debug.Assert(layers is not null);
            if ((mask & (mask - 1)) != 0)
                return;

            var array = layers!;
            if (mask == 0)
            {
                single = default;
                singleLayer = 0;
            }
            else
            {
                singleLayer = BitOperations.TrailingZeroCount(mask);
                single = array[singleLayer].Take();
            }

            layers = null;
            mask = 0;
            pool?.Return(array);
        }

        /// <summary>
        /// Disposes the nonempty layers of a multi-layer subset and returns its array to <paramref name="pool"/>, leaving
        /// an empty single-layer subset.
        /// </summary>
        void ReleaseLayers(SubsetArrayPool? pool)
        {
            var array = layers!;
            var remaining = mask;
            while (remaining != 0)
            {
                var l = BitOperations.TrailingZeroCount(remaining);
                remaining &= remaining - 1;
                array[l].Dispose();
            }

            layers = null;
            mask = 0;
            single = default;
            singleLayer = 0;
            pool?.Return(array);
        }

        [DoesNotReturn]
        [MethodImpl(MethodImplOptions.NoInlining)]
        static void ThrowLayerOutOfRange(int layer)
        {
            throw new ArgumentOutOfRangeException(nameof(layer), layer, $"A layer lies in [0, {MaxLayers}).");
        }

        [DoesNotReturn]
        [MethodImpl(MethodImplOptions.NoInlining)]
        static void ThrowMultiLayer()
        {
            throw new InvalidOperationException("The subset spans several layers; read it per layer with GetLayer.");
        }

    }

}

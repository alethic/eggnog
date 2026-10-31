using System;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Text;

namespace Eggnog.Relations
{

    /// <summary>
    /// An owned set of rows of one table as a context sees it: a <see cref="Subset"/> per layer of the table's view,
    /// the row set of every trie node, header and probe result of a layered run. Rows of different layers are different
    /// rows, even with equal ids.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Representation. While the rows lie in at most one layer, the subset is <em>single</em>: one <see cref="Subset"/>
    /// and its layer, which is what every root subset is (layer 0), so the root path is a plain subset behind one null
    /// test. Once two or more layers are nonempty it is <em>multi-layer</em>: an array of per-layer subsets indexed by
    /// layer, whose entry is <see cref="Subset.Empty"/> exactly when the layer holds no rows, with a 64-bit mask of the
    /// nonempty layers below <see cref="MaskedLayers"/>. Every operation that can empty layers collapses back: a
    /// multi-layer subset always has at least two nonempty layers. <c>default</c> is <see cref="Empty"/>.
    /// </para>
    /// <para>
    /// Layers. Any number: one per level of the context chain, which has no depth limit. The mask makes walking the
    /// layers of a chain up to 63 deep one trailing-zero count per nonempty layer; a deeper chain's layers from
    /// <see cref="MaskedLayers"/> on are found by testing their entries (<see cref="GetLayers"/>), with no further
    /// storage. The mask's upper half lives in the field that holds a single-layer subset's layer, so the struct stays
    /// four words.
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
    /// Not thread-safe for mutation; any number of threads may read one nobody mutates.
    /// </para>
    /// </remarks>
    internal struct LayeredSubset
    {

        /// <summary>
        /// The layers a multi-layer subset's mask covers: <c>[0, 64)</c>. Layers from here on are found by their entries.
        /// </summary>
        public const int MaskedLayers = 64;

        Subset single;
        Subset[]? layers;

        /// <summary>
        /// The layer of a single-layer subset; bits 32 to 63 of the mask of a multi-layer one.
        /// </summary>
        int layerOrHighMask;

        /// <summary>
        /// Bits 0 to 31 of the mask of a multi-layer subset; 0 for a single-layer one.
        /// </summary>
        uint lowMask;

        /// <summary>
        /// Initializes a single-layer subset.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        LayeredSubset(int layer, Subset rows)
        {
            single = rows;
            layers = null;
            layerOrHighMask = layer;
            lowMask = 0;
        }

        /// <summary>
        /// Initializes a multi-layer subset over <paramref name="layers"/>, whose nonempty entries below
        /// <see cref="MaskedLayers"/> are <paramref name="mask"/>; the caller collapses it if fewer than two entries are
        /// nonempty.
        /// </summary>
        LayeredSubset(Subset[] layers, ulong mask)
        {
            single = default;
            this.layers = layers;
            layerOrHighMask = (int)(uint)(mask >> 32);
            lowMask = (uint)mask;
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
        /// Gets one more than the highest layer that holds rows (0 when empty): the length a per-layer array of a copy
        /// needs. O(1), except for a multi-layer subset whose array is longer than <see cref="MaskedLayers"/>, whose
        /// entries from there on are tested from the top down.
        /// </summary>
        public readonly int TopLayer
        {
            get
            {
                if (layers is null)
                    return single.IsEmpty ? 0 : layerOrHighMask + 1;

                for (var l = layers.Length - 1; l >= MaskedLayers; l--)
                    if (layers[l].IsEmpty == false)
                        return l + 1;

                return 64 - BitOperations.LeadingZeroCount(Mask);
            }
        }

        /// <summary>
        /// Gets the layer of a single-layer subset (0 for <see cref="Empty"/>), or −1 for a multi-layer one.
        /// </summary>
        public readonly int SingleLayer
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => layers is null ? layerOrHighMask : -1;
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
        /// <param name="layer">The layer, not negative.</param>
        /// <param name="rows">The rows, whose ownership moves to the result.</param>
        /// <returns>The subset.</returns>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="layer"/> is negative.</exception>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static LayeredSubset Single(int layer, Subset rows)
        {
            if (layer < 0)
                ThrowLayerOutOfRange(layer);

            return new LayeredSubset(layer, rows);
        }

        /// <summary>
        /// Creates a subset from per-layer rows: entry <c>l</c> of <paramref name="layers"/> is the rows of layer
        /// <c>l</c>. Collapses to a single-layer subset (or <see cref="Empty"/>) when fewer than two entries are nonempty,
        /// returning the array to <paramref name="pool"/>; empty entries are disposed. O(array length).
        /// </summary>
        /// <param name="layers">The per-layer rows, of any length; the array and every entry move to the result (the
        /// array to <paramref name="pool"/> when it collapses).</param>
        /// <param name="pool">The pool the array returns to once the subset no longer needs it, or <c>null</c>.</param>
        /// <returns>The subset.</returns>
        public static LayeredSubset FromLayers(Subset[] layers, SubsetArrayPool? pool)
        {
            ArgumentNullException.ThrowIfNull(layers);
            var nonempty = 0UL;
            for (var l = 0; l < layers.Length; l++)
            {
                if (layers[l].IsEmpty)
                    layers[l].Dispose();
                else if (l < MaskedLayers)
                    nonempty |= 1UL << l;
            }

            var result = new LayeredSubset(layers, nonempty);
            result.Collapse(pool);
            return result;
        }

        /// <summary>
        /// Borrows the rows of layer <paramref name="layer"/>: empty when the layer holds none. Valid until the subset is
        /// next mutated or disposed. O(1).
        /// </summary>
        /// <param name="layer">The layer, not negative; a layer past the subset's holds no rows.</param>
        /// <returns>The layer's rows.</returns>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="layer"/> is negative.</exception>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public readonly SubsetRef GetLayer(int layer)
        {
            if (layer < 0)
                ThrowLayerOutOfRange(layer);

            if (layers is null)
                return layer == layerOrHighMask ? single.AsRef() : default;

            // An entry is empty exactly when its layer holds no rows, so the entry answers without the mask.
            return (uint)layer < (uint)layers.Length ? layers[layer].AsRef() : default;
        }

        /// <summary>
        /// Gets the layers that hold rows, in ascending order: none for <see cref="Empty"/>, the one layer of a nonempty
        /// single-layer subset. Valid until the subset is next mutated or disposed, except that the caller may empty the
        /// layer it was just given.
        /// </summary>
        /// <returns>The enumerator.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public readonly LayerEnumerator GetLayers()
        {
            if (layers is null)
                return single.IsEmpty ? LayerEnumerator.Empty : LayerEnumerator.Single(layerOrHighMask);

            return LayerEnumerator.Multi(Mask, layers);
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
                    if (layerOrHighMask == other.layerOrHighMask)
                        single.Intersect(other.single.AsRef());
                    else
                        single.Dispose();

                    return;
                }

                var theirs = other.GetLayer(layerOrHighMask);
                if (theirs.IsEmpty == false)
                    single.Intersect(theirs);
                else
                    single.Dispose();

                return;
            }

            if (other.layers is null)
            {
                // Only the other side's layer can survive: keep it, release the rest.
                var layer = other.layerOrHighMask;
                var kept = Subset.Empty;
                if (GetLayer(layer).IsEmpty == false)
                {
                    kept = layers[layer].Take();
                    kept.Intersect(other.single.AsRef());
                }

                ReleaseLayers(pool);
                single = kept;
                layerOrHighMask = layer;
                return;
            }

            var survivors = 0UL;
            foreach (var l in GetLayers())
            {
                var theirs = other.GetLayer(l);
                if (theirs.IsEmpty == false)
                {
                    layers[l].Intersect(theirs);
                    if (layers[l].IsEmpty)
                        layers[l].Dispose();
                    else if (l < MaskedLayers)
                        survivors |= 1UL << l;
                }
                else
                {
                    layers[l].Dispose();
                }
            }

            Mask = survivors;
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
                return new LayeredSubset(layerOrHighMask, single.Clone());

            var copy = RentArray(pool, TopLayer);
            foreach (var l in GetLayers())
                copy[l] = layers[l].Clone();

            return new LayeredSubset(copy, Mask);
        }

        /// <summary>
        /// Enumerates the rows of a single-layer subset (every root subset, so root code reads a header or node as it
        /// reads a plain subset). A multi-layer subset is read per layer (<see cref="GetLayer"/>), since its row ids
        /// repeat across layers.
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

            layer = layerOrHighMask;
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
                return $"L{layerOrHighMask}:{single}";

            var text = new StringBuilder("{");
            foreach (var l in GetLayers())
            {
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
        /// Gets or sets the mask of a multi-layer subset: bit <c>l</c> is set when layer <c>l</c>, below
        /// <see cref="MaskedLayers"/>, holds rows.
        /// </summary>
        ulong Mask
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            readonly get => ((ulong)(uint)layerOrHighMask << 32) | lowMask;
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            set
            {
                layerOrHighMask = (int)(uint)(value >> 32);
                lowMask = (uint)value;
            }
        }

        /// <summary>
        /// Sums the sizes of the nonempty layers of a multi-layer subset.
        /// </summary>
        readonly int MultiSize()
        {
            var total = 0;
            foreach (var l in GetLayers())
                total += layers![l].Size;

            return total;
        }

        /// <summary>
        /// Turns a multi-layer subset with fewer than two nonempty layers into a single-layer one (or
        /// <see cref="Empty"/>), returning its array to <paramref name="pool"/>. O(1) when two masked layers hold rows or
        /// the array is no longer than <see cref="MaskedLayers"/>; otherwise the entries from there on are tested until a
        /// second nonempty layer turns up.
        /// </summary>
        void Collapse(SubsetArrayPool? pool)
        {
            Debug.Assert(layers is not null);
            var mask = Mask;
            if ((mask & (mask - 1)) != 0)
                return;

            var array = layers!;
            var only = mask == 0 ? -1 : BitOperations.TrailingZeroCount(mask);
            for (var l = MaskedLayers; l < array.Length; l++)
            {
                if (array[l].IsEmpty)
                    continue;

                if (only >= 0)
                    return;

                only = l;
            }

            single = only < 0 ? default : array[only].Take();
            layerOrHighMask = Math.Max(only, 0);
            lowMask = 0;
            layers = null;
            pool?.Return(array);
        }

        /// <summary>
        /// Disposes the nonempty layers of a multi-layer subset and returns its array to <paramref name="pool"/>, leaving
        /// an empty single-layer subset.
        /// </summary>
        void ReleaseLayers(SubsetArrayPool? pool)
        {
            var array = layers!;
            foreach (var l in GetLayers())
                array[l].Dispose();

            layers = null;
            lowMask = 0;
            single = default;
            layerOrHighMask = 0;
            pool?.Return(array);
        }

        [DoesNotReturn]
        [MethodImpl(MethodImplOptions.NoInlining)]
        static void ThrowLayerOutOfRange(int layer)
        {
            throw new ArgumentOutOfRangeException(nameof(layer), layer, "A layer is not negative.");
        }

        [DoesNotReturn]
        [MethodImpl(MethodImplOptions.NoInlining)]
        static void ThrowMultiLayer()
        {
            throw new InvalidOperationException("The subset spans several layers; read it per layer with GetLayer.");
        }

    }

}

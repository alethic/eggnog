using System;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace Eggnog.Relations
{

    internal sealed partial class TableView
    {

        /// <summary>
        /// Scratch for keys of more than three cells passed as spans. One per thread; lookups are reads and run on many
        /// threads.
        /// </summary>
        [ThreadStatic]
        static uint[]? scratch;

        /// <summary>
        /// Finds the visible row with the one-cell key <paramref name="k0"/> (§6.5, the read phase): probes the layers
        /// from the top down, and the first layer whose key index holds the key decides (Lemma F): its row if the view
        /// does not hide it, otherwise none. No allocation; one hash probe per present layer, plus a sealed-set probe on
        /// a hit.
        /// </summary>
        /// <param name="k0">The key.</param>
        /// <param name="layer">The depth of the layer holding the row, or −1.</param>
        /// <returns>The row id in that layer, or −1.</returns>
        public int FindRow(uint k0, out int layer)
        {
            var layers = Layers;
            for (var j = layers.Length - 1; j >= 0; j--)
            {
                if (layers[j] is not { } lv)
                    continue;

                var row = lv.Sorted!.FindRow(k0);
                if (row < 0)
                    continue;

                if (lv.Hidden.Contains(row))
                    break;

                layer = j;
                return row;
            }

            layer = -1;
            return -1;
        }

        /// <summary>
        /// Finds the visible row with the two-cell key (<paramref name="k0"/>, <paramref name="k1"/>); see
        /// <see cref="FindRow(uint, out int)"/>.
        /// </summary>
        /// <param name="k0">The first key cell.</param>
        /// <param name="k1">The second key cell.</param>
        /// <param name="layer">The depth of the layer holding the row, or −1.</param>
        /// <returns>The row id in that layer, or −1.</returns>
        public int FindRow(uint k0, uint k1, out int layer)
        {
            var layers = Layers;
            for (var j = layers.Length - 1; j >= 0; j--)
            {
                if (layers[j] is not { } lv)
                    continue;

                var row = lv.Sorted!.FindRow(k0, k1);
                if (row < 0)
                    continue;

                if (lv.Hidden.Contains(row))
                    break;

                layer = j;
                return row;
            }

            layer = -1;
            return -1;
        }

        /// <summary>
        /// Finds the visible row with the three-cell key; see <see cref="FindRow(uint, out int)"/>.
        /// </summary>
        /// <param name="k0">The first key cell.</param>
        /// <param name="k1">The second key cell.</param>
        /// <param name="k2">The third key cell.</param>
        /// <param name="layer">The depth of the layer holding the row, or −1.</param>
        /// <returns>The row id in that layer, or −1.</returns>
        public int FindRow(uint k0, uint k1, uint k2, out int layer)
        {
            var layers = Layers;
            for (var j = layers.Length - 1; j >= 0; j--)
            {
                if (layers[j] is not { } lv)
                    continue;

                var row = lv.Sorted!.FindRow(k0, k1, k2);
                if (row < 0)
                    continue;

                if (lv.Hidden.Contains(row))
                    break;

                layer = j;
                return row;
            }

            layer = -1;
            return -1;
        }

        /// <summary>
        /// Finds the visible row whose key is the <see cref="TableSpec.KeyCount"/> cells starting at
        /// <paramref name="key"/>[<paramref name="offset"/>]: the form for compiled plans and wide keys. The key is
        /// hashed once for every layer. See <see cref="FindRow(uint, out int)"/>.
        /// </summary>
        /// <param name="key">The array holding the key.</param>
        /// <param name="offset">The index of the key's first cell.</param>
        /// <param name="layer">The depth of the layer holding the row, or −1.</param>
        /// <returns>The row id in that layer, or −1.</returns>
        public int FindRow(uint[] key, int offset, out int layer)
        {
            ArgumentNullException.ThrowIfNull(key);
            var keyCount = Spec.KeyCount;
            if (offset < 0 || key.Length - offset < keyCount)
                throw new ArgumentOutOfRangeException(nameof(offset), offset, "The array does not hold a whole key at the offset.");

            var keyHash = TableKeyHash.Hash(key, offset, keyCount);
            var layers = Layers;
            for (var j = layers.Length - 1; j >= 0; j--)
            {
                if (layers[j] is not { } lv)
                    continue;

                var row = lv.Sorted!.FindRowHashed(key, offset, keyHash);
                if (row < 0)
                    continue;

                if (lv.Hidden.Contains(row))
                    break;

                layer = j;
                return row;
            }

            layer = -1;
            return -1;
        }

        /// <summary>
        /// Finds the visible row with key <paramref name="key"/>; see <see cref="FindRow(uint, out int)"/>. Keys of up to
        /// three cells are probed from registers, wider ones from a per-thread scratch array.
        /// </summary>
        /// <param name="key">The key; exactly <see cref="TableSpec.KeyCount"/> cells.</param>
        /// <param name="layer">The depth of the layer holding the row, or −1.</param>
        /// <returns>The row id in that layer, or −1.</returns>
        public int FindRow(ReadOnlySpan<uint> key, out int layer)
        {
            var keyCount = Spec.KeyCount;
            if (key.Length != keyCount)
                throw new ArgumentException($"Expected a key of {keyCount} cells but got {key.Length}.", nameof(key));

            switch (keyCount)
            {
                case 0:
                    return FindRow(Array.Empty<uint>(), 0, out layer);
                case 1:
                    return FindRow(key[0], out layer);
                case 2:
                    return FindRow(key[0], key[1], out layer);
                case 3:
                    return FindRow(key[0], key[1], key[2], out layer);
                default:
                    var buffer = Scratch(keyCount);
                    key.CopyTo(buffer);
                    return FindRow(buffer, 0, out layer);
            }
        }

        /// <summary>
        /// Gets column <paramref name="column"/> of the visible row with the one-cell key <paramref name="k0"/>.
        /// </summary>
        /// <param name="k0">The key.</param>
        /// <param name="column">The column.</param>
        /// <param name="value">The value, or zero.</param>
        /// <returns><c>true</c> if a visible row holds the key.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool TryGetColumn(uint k0, int column, out uint value) => ReadColumn(FindRow(k0, out var layer), layer, column, out value);

        /// <summary>
        /// Gets column <paramref name="column"/> of the visible row with the two-cell key.
        /// </summary>
        /// <param name="k0">The first key cell.</param>
        /// <param name="k1">The second key cell.</param>
        /// <param name="column">The column.</param>
        /// <param name="value">The value, or zero.</param>
        /// <returns><c>true</c> if a visible row holds the key.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool TryGetColumn(uint k0, uint k1, int column, out uint value) => ReadColumn(FindRow(k0, k1, out var layer), layer, column, out value);

        /// <summary>
        /// Gets column <paramref name="column"/> of the visible row with the three-cell key.
        /// </summary>
        /// <param name="k0">The first key cell.</param>
        /// <param name="k1">The second key cell.</param>
        /// <param name="k2">The third key cell.</param>
        /// <param name="column">The column.</param>
        /// <param name="value">The value, or zero.</param>
        /// <returns><c>true</c> if a visible row holds the key.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool TryGetColumn(uint k0, uint k1, uint k2, int column, out uint value) => ReadColumn(FindRow(k0, k1, k2, out var layer), layer, column, out value);

        /// <summary>
        /// Gets column <paramref name="column"/> of the visible row whose key starts at
        /// <paramref name="key"/>[<paramref name="offset"/>].
        /// </summary>
        /// <param name="key">The array holding the key.</param>
        /// <param name="offset">The index of the key's first cell.</param>
        /// <param name="column">The column.</param>
        /// <param name="value">The value, or zero.</param>
        /// <returns><c>true</c> if a visible row holds the key.</returns>
        public bool TryGetColumn(uint[] key, int offset, int column, out uint value) => ReadColumn(FindRow(key, offset, out var layer), layer, column, out value);

        /// <summary>
        /// Gets column <paramref name="column"/> of the visible row with key <paramref name="key"/>.
        /// </summary>
        /// <param name="key">The key.</param>
        /// <param name="column">The column.</param>
        /// <param name="value">The value, or zero.</param>
        /// <returns><c>true</c> if a visible row holds the key.</returns>
        public bool TryGetColumn(ReadOnlySpan<uint> key, int column, out uint value) => ReadColumn(FindRow(key, out var layer), layer, column, out value);

        /// <summary>
        /// Copies the visible row with key <paramref name="key"/> into <paramref name="destination"/>.
        /// </summary>
        /// <param name="key">The key.</param>
        /// <param name="destination">Receives the row; at least as many cells as the table has columns.</param>
        /// <param name="layer">The depth of the layer holding the row, or −1.</param>
        /// <param name="row">The row id in that layer, or −1.</param>
        /// <returns><c>true</c> if a visible row holds the key.</returns>
        public bool TryGetRow(ReadOnlySpan<uint> key, Span<uint> destination, out int layer, out int row)
        {
            var arity = Spec.Arity;
            if (destination.Length < arity)
                throw new ArgumentException($"The destination needs room for {arity} cells.", nameof(destination));

            row = FindRow(key, out layer);
            if (row < 0)
                return false;

            Layers[layer]!.Sorted!.Rows.GetRow(row).CopyTo(destination);
            return true;
        }

        /// <summary>
        /// Reads one cell of a found row, or reports absence.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        bool ReadColumn(int row, int layer, int column, out uint value)
        {
            if ((uint)column >= (uint)Spec.Arity)
                ThrowColumn(column, Spec.Arity);

            if (row < 0)
            {
                value = 0;
                return false;
            }

            var sorted = Layers[layer]!.Sorted!;
            value = sorted.Rows.Data[row * sorted.Arity + column];
            return true;
        }

        /// <summary>
        /// Gets this thread's scratch array, at least <paramref name="length"/> cells long.
        /// </summary>
        static uint[] Scratch(int length)
        {
            var buffer = scratch;
            if (buffer is null || buffer.Length < length)
                scratch = buffer = new uint[Math.Max(length, 8)];

            return buffer;
        }

        [DoesNotReturn]
        [MethodImpl(MethodImplOptions.NoInlining)]
        static void ThrowColumn(int column, int arity)
        {
            throw new ArgumentOutOfRangeException(nameof(column), column, $"The table has {arity} columns.");
        }

    }

}

using System;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// The key of a row of a bridge differential function, of any length: its first key cells, compared by value. The
    /// oracle (<see cref="BridgeDiffOracle"/>) and the comparison (<see cref="BridgeDiffComparer"/>) index rows by it.
    /// </summary>
    /// <remarks>
    /// The key wraps the array it is given, without copying it, and hashes its first <see cref="Count"/> cells once:
    /// the owner must not change them while the key is in a dictionary (a row's cells, which views never change, or a
    /// fresh array), and a probe over a scratch buffer must only be looked up, not stored. A function without keys (an
    /// egglog global) has the empty key, so its table holds at most one row.
    /// </remarks>
    readonly struct BridgeDiffKey : IEquatable<BridgeDiffKey>
    {

        readonly uint[] cells;
        readonly int count;
        readonly int hash;

        /// <summary>
        /// Initializes a key of the first <paramref name="count"/> cells of <paramref name="row"/>, which it wraps.
        /// </summary>
        /// <param name="row">The cells, keys first; not copied.</param>
        /// <param name="count">The number of key cells.</param>
        public BridgeDiffKey(uint[] row, int count)
        {
            ArgumentOutOfRangeException.ThrowIfGreaterThan(count, row.Length);
            cells = row;
            this.count = count;
            var h = new HashCode();
            for (var i = 0; i < count; i++)
                h.Add(row[i]);

            hash = h.ToHashCode();
        }

        /// <summary>
        /// Gets the number of key cells.
        /// </summary>
        public int Count => count;

        /// <summary>
        /// Gets the key cells.
        /// </summary>
        public ReadOnlySpan<uint> Cells => cells.AsSpan(0, count);

        /// <summary>
        /// Gets key cell <paramref name="index"/>.
        /// </summary>
        /// <param name="index">The cell's index, below <see cref="Count"/>.</param>
        /// <returns>The cell.</returns>
        public uint this[int index] => cells[index];

        /// <inheritdoc />
        public bool Equals(BridgeDiffKey other) => hash == other.hash && Cells.SequenceEqual(other.Cells);

        /// <inheritdoc />
        public override bool Equals(object? obj) => obj is BridgeDiffKey other && Equals(other);

        /// <inheritdoc />
        public override int GetHashCode() => hash;

        /// <inheritdoc />
        public override string ToString() => "(" + string.Join(", ", Cells.ToArray()) + ")";

    }

}

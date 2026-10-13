using System;

namespace Eggnog.Relations
{

    /// <summary>
    /// What a split is about (colored-contexts spec §11.3, B6; rule-layer E8): the index of the source that asked for it
    /// and the cells that identify the split (for a rule split, its key variables' values). Within one driver, two requests
    /// with equal keys split the same thing. Across drivers the index means nothing (each driver numbers its own sources
    /// from 0), so B6 coverage compares the record's source object by reference and the cells
    /// (<see cref="SplitRecord.Splits"/>): a split covered by a live record in a scope or any of its ancestors is not split
    /// again.
    /// </summary>
    /// <remarks>
    /// The key takes ownership of <c>values</c>: the caller must not change the array afterwards. Equality compares the
    /// source and the cells; the hash is computed once, at construction.
    /// </remarks>
    internal readonly struct SplitKey : IEquatable<SplitKey>
    {

        readonly uint[] values;
        readonly int hash;

        /// <summary>
        /// Initializes a key of source <paramref name="source"/> over <paramref name="values"/>.
        /// </summary>
        /// <param name="source">The source's index in its driver's source list.</param>
        /// <param name="values">The identifying cells; owned by the key from now on.</param>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="source"/> is negative.</exception>
        public SplitKey(int source, uint[] values)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(source);
            ArgumentNullException.ThrowIfNull(values);
            Source = source;
            this.values = values;
            hash = HashCode.Combine(source, FxHasher.Hash(values));
        }

        /// <summary>
        /// Gets the index of the source that asked for the split.
        /// </summary>
        public int Source { get; }

        /// <summary>
        /// Gets the identifying cells.
        /// </summary>
        public ReadOnlySpan<uint> Values => values;

        /// <inheritdoc />
        public bool Equals(SplitKey other) => Source == other.Source && hash == other.hash && Values.SequenceEqual(other.Values);

        /// <inheritdoc />
        public override bool Equals(object? obj) => obj is SplitKey other && Equals(other);

        /// <inheritdoc />
        public override int GetHashCode() => hash;

        /// <summary>
        /// Compares two keys for equality.
        /// </summary>
        public static bool operator ==(SplitKey left, SplitKey right) => left.Equals(right);

        /// <summary>
        /// Compares two keys for inequality.
        /// </summary>
        public static bool operator !=(SplitKey left, SplitKey right) => left.Equals(right) == false;

        /// <inheritdoc />
        public override string ToString() => $"SplitKey({Source}: {string.Join(", ", values ?? Array.Empty<uint>())})";

    }

}

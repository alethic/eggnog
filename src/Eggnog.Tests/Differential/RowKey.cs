using System;

namespace Eggnog.Tests.Differential
{

    /// <summary>
    /// A tuple of decoded cells compared by content: the key of a reference table's row, or a match's projection.
    /// </summary>
    internal readonly struct RowKey : IEquatable<RowKey>
    {

        readonly int hash;

        /// <summary>
        /// Initializes a key over <paramref name="cells"/>, which the key now owns.
        /// </summary>
        /// <param name="cells">The cells: <c>long</c>, <c>string</c>, <c>Set&lt;int&gt;</c>, or a class
        /// id.</param>
        public RowKey(object[] cells)
        {
            Cells = cells;
            var combined = new HashCode();
            foreach (var cell in cells)
                combined.Add(cell);

            hash = combined.ToHashCode();
        }

        /// <summary>Gets the cells.</summary>
        public object[] Cells { get; }

        /// <inheritdoc />
        public bool Equals(RowKey other)
        {
            if (hash != other.hash || Cells.Length != other.Cells.Length)
                return false;

            for (var i = 0; i < Cells.Length; i++)
                if (Equals(Cells[i], other.Cells[i]) == false)
                    return false;

            return true;
        }

        /// <inheritdoc />
        public override bool Equals(object? obj) => obj is RowKey other && Equals(other);

        /// <inheritdoc />
        public override int GetHashCode() => hash;

    }

}

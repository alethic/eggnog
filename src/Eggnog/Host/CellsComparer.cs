using System;
using System.Collections.Generic;

namespace Eggnog
{

    /// <summary>
    /// Equality of rows of cells, element by element.
    /// </summary>
    internal sealed class CellsComparer : IEqualityComparer<uint[]>
    {

        /// <summary>
        /// Gets the comparer.
        /// </summary>
        public static CellsComparer Instance { get; } = new();

        CellsComparer()
        {
        }

        /// <inheritdoc />
        public bool Equals(uint[]? x, uint[]? y) => ReferenceEquals(x, y) || (x is not null && y is not null && x.AsSpan().SequenceEqual(y));

        /// <inheritdoc />
        public int GetHashCode(uint[] obj)
        {
            var hash = new HashCode();
            foreach (var cell in obj)
                hash.Add(cell);

            return hash.ToHashCode();
        }

    }

}

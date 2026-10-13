using System;
using System.Collections.Generic;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// Compares rows and keys (<c>uint[]</c>) by content, for the dictionaries of the differential model.
    /// </summary>
    sealed class DiffRowComparer : IEqualityComparer<uint[]>
    {

        /// <summary>
        /// Gets the shared instance.
        /// </summary>
        public static DiffRowComparer Instance { get; } = new();

        /// <inheritdoc />
        public bool Equals(uint[]? x, uint[]? y) => ReferenceEquals(x, y) || (x is not null && y is not null && x.AsSpan().SequenceEqual(y));

        /// <inheritdoc />
        public int GetHashCode(uint[] obj)
        {
            var h = new HashCode();
            foreach (var cell in obj)
                h.Add(cell);

            return h.ToHashCode();
        }

    }

}

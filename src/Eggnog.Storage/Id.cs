using System;

namespace Eggnog.Storage
{

    /// <summary>
    /// Identifies an e-class. Identifiers are allocated by a <see cref="Store"/> and are unique across all of
    /// its contexts, so an identifier made in one context never collides with one made in another.
    /// </summary>
    /// <remarks>
    /// In a row an identifier is stored as its <see cref="Value"/> widened to a <see cref="ulong"/>; use
    /// <see cref="ToValue"/> and <see cref="FromValue"/> to move between the two.
    /// </remarks>
    /// <param name="Value">The raw identifier.</param>
    public readonly record struct Id(uint Value)
    {

        /// <summary>
        /// Reads an identifier out of a row value.
        /// </summary>
        /// <param name="value">The row value.</param>
        /// <returns>The identifier.</returns>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="value"/> is not an identifier.</exception>
        public static Id FromValue(ulong value) => value <= uint.MaxValue ? new Id((uint)value) : throw new ArgumentOutOfRangeException(nameof(value));

        /// <summary>
        /// Returns the identifier as a row value.
        /// </summary>
        /// <returns>The row value.</returns>
        public ulong ToValue() => Value;

        /// <inheritdoc />
        public override string ToString() => "#" + Value;

    }

}

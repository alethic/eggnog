using System;
using System.Globalization;

namespace Eggnog
{

    /// <summary>
    /// An e-class identifier, as a scope returned it. Only the library makes one: <c>default(ClassId)</c> names no
    /// class and every scope rejects it. A value is usable in the scope that returned it and that scope's descendants;
    /// once that scope is disposed it refuses new calls itself, and the value stays usable in its live descendants.
    /// Two values are equal when they belong to one graph and hold the same identifier; members of one class may
    /// differ, so compare classes with <see cref="Scope.AreEqual{TSort}(TSort, TSort)"/>. Renders as <c>#17</c>.
    /// </summary>
    public readonly struct ClassId : IEquatable<ClassId>
    {

        readonly uint cell;
        readonly ScopeOrigin? origin;

        /// <summary>
        /// Initializes a value for <paramref name="cell"/>, returned by the scope or batch <paramref name="origin"/>.
        /// </summary>
        /// <param name="cell">The class's engine cell.</param>
        /// <param name="origin">The origin of the scope or batch returning it.</param>
        internal ClassId(uint cell, ScopeOrigin origin)
        {
            this.cell = cell;
            this.origin = origin;
        }

        /// <summary>
        /// Gets whether this is <c>default(ClassId)</c>, which names no class.
        /// </summary>
        public bool IsDefault => origin is null;

        /// <summary>
        /// Gets the class's engine cell. Meaningless for <c>default(ClassId)</c>.
        /// </summary>
        internal uint Cell => cell;

        /// <summary>
        /// Gets the origin of the scope or batch that returned the value; <c>null</c> for <c>default(ClassId)</c>.
        /// </summary>
        internal ScopeOrigin? Origin => origin;

        /// <summary>
        /// Gets whether <paramref name="other"/> belongs to the same graph and holds the same identifier.
        /// </summary>
        /// <param name="other">The other value.</param>
        /// <returns>Whether the values are equal.</returns>
        public bool Equals(ClassId other) => cell == other.cell && ReferenceEquals(origin?.Graph, other.origin?.Graph);

        /// <inheritdoc />
        public override bool Equals(object? obj) => obj is ClassId other && Equals(other);

        /// <inheritdoc />
        public override int GetHashCode() => HashCode.Combine(cell, origin?.Graph);

        /// <summary>
        /// Gets whether two values are equal (<see cref="Equals(ClassId)"/>).
        /// </summary>
        /// <param name="left">One value.</param>
        /// <param name="right">The other value.</param>
        /// <returns>Whether they are equal.</returns>
        public static bool operator ==(ClassId left, ClassId right) => left.Equals(right);

        /// <summary>
        /// Gets whether two values differ (<see cref="Equals(ClassId)"/>).
        /// </summary>
        /// <param name="left">One value.</param>
        /// <param name="right">The other value.</param>
        /// <returns>Whether they differ.</returns>
        public static bool operator !=(ClassId left, ClassId right) => left.Equals(right) == false;

        /// <summary>
        /// Renders the value: <c>#17</c>, or <c>default(ClassId)</c>.
        /// </summary>
        /// <returns>The rendering.</returns>
        public override string ToString() => IsDefault ? "default(ClassId)" : "#" + cell.ToString(CultureInfo.InvariantCulture);

    }

}

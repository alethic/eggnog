using System;

namespace Eggnog.Tests.Support
{

    /// <summary>
    /// A comparable registered value type, so that sets of it can be registered (<see cref="ValueStore.RegisterSet{T}"/>).
    /// </summary>
    /// <param name="Value">The value.</param>
    public readonly record struct Ranked(int Value) : IComparable<Ranked>
    {

        /// <inheritdoc />
        public int CompareTo(Ranked other) => Value.CompareTo(other.Value);

    }

}

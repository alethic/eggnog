using System;
using System.Collections;
using System.Collections.Generic;

namespace Eggnog
{

    /// <summary>
    /// One type repeated a number of times, as a read-only list: the parameter types of a variadic primitive applied to
    /// that many arguments, which cost one object whatever the arity, where a primitive cached per arity would otherwise
    /// keep an array per arity.
    /// </summary>
    internal sealed class RepeatedTypes : IReadOnlyList<Type>
    {

        readonly Type type;

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="type">The type.</param>
        /// <param name="count">How many times it repeats.</param>
        public RepeatedTypes(Type type, int count)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(count);
            this.type = type;
            Count = count;
        }

        /// <inheritdoc />
        public int Count { get; }

        /// <inheritdoc />
        public Type this[int index] => (uint)index < (uint)Count ? type : throw new ArgumentOutOfRangeException(nameof(index));

        /// <inheritdoc />
        public IEnumerator<Type> GetEnumerator()
        {
            for (var i = 0; i < Count; i++)
                yield return type;
        }

        /// <inheritdoc />
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    }

}

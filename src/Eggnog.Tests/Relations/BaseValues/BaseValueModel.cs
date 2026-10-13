using System;
using System.Collections.Generic;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// A reference model of egglog's encoding of one base value type, written from the Rust source
    /// (core-relations/src/base_values/mod.rs:158-188, unboxed.rs) rather than from the port: a value is stored inline
    /// when <c>inline</c> says so, and otherwise gets the next intern index the first time it is seen, tagged with
    /// <c>0x8000_0000</c> when the type boxes some values inline. Calls must be made in the same order as on the
    /// registry under test, so that intern indices line up.
    /// </summary>
    /// <typeparam name="T">The base value type.</typeparam>
    public sealed class BaseValueModel<T>
        where T : notnull
    {

        readonly Func<T, uint?> inline;
        readonly bool tagged;
        readonly Dictionary<T, uint> indices = new();
        readonly List<T> interned = new();

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="inline">The inline representation of a value, or <c>null</c> when it must be interned.</param>
        /// <param name="tagged">Whether interned values are tagged (egglog's <c>MAY_UNBOX</c>).</param>
        public BaseValueModel(Func<T, uint?> inline, bool tagged)
        {
            this.inline = inline;
            this.tagged = tagged;
        }

        /// <summary>
        /// Gets the number of interned values.
        /// </summary>
        public int InternedCount => interned.Count;

        /// <summary>
        /// Gets every interned value, in intern order.
        /// </summary>
        public IReadOnlyList<T> Interned => interned;

        /// <summary>
        /// Gets the expected representation of <paramref name="value"/>, interning it in the model if it is new.
        /// </summary>
        /// <param name="value">The value.</param>
        /// <returns>The expected representation.</returns>
        public uint Box(T value)
        {
            if (inline(value) is uint rep)
                return rep;

            if (indices.TryGetValue(value, out var index) == false)
            {
                index = (uint)interned.Count;
                indices.Add(value, index);
                interned.Add(value);
            }

            return tagged ? 0x8000_0000u + index : index;
        }

        /// <summary>
        /// Gets the value that unwrapping the representation of <paramref name="value"/> yields: the value itself when
        /// it is inline, otherwise the first equal value interned.
        /// </summary>
        /// <param name="value">A value already passed to <see cref="Box"/>.</param>
        /// <returns>The representative.</returns>
        public T Representative(T value) => inline(value) is null ? interned[(int)indices[value]] : value;

    }

}

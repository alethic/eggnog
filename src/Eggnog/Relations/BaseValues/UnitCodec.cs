using System;
using System.Runtime.CompilerServices;

namespace Eggnog.Relations
{

    /// <summary>
    /// The codec for the unit base value, Rust's <c>()</c>, represented by the empty <see cref="ValueTuple"/>: always
    /// stored as 0, and every value reads back as unit (core-relations/src/base_values/unboxed.rs:54-69). Never touches an
    /// intern table.
    /// </summary>
    internal readonly struct UnitCodec : IBaseValueCodec<ValueTuple>
    {

        /// <inheritdoc />
        public static bool MayUnbox => true;

        /// <inheritdoc />
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool TryBox(ValueTuple value, out Value boxed)
        {
            boxed = new Value(0);
            return true;
        }

        /// <inheritdoc />
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool TryUnbox(Value boxed, out ValueTuple value)
        {
            value = default;
            return true;
        }

        /// <summary>
        /// Formats the value as Rust's <c>Debug</c> does: <c>()</c>.
        /// </summary>
        /// <param name="value">The value.</param>
        /// <returns>The text.</returns>
        public static string Format(ValueTuple value) => "()";

    }

}

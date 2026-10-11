using System.Globalization;
using System.Runtime.CompilerServices;

namespace Eggnog.Relations
{

    /// <summary>
    /// The codec for <see cref="nuint"/> base values (Rust <c>usize</c>): values in <c>[0, 2^31)</c> are stored inline as
    /// themselves; all others are interned and stored as <see cref="BaseValues.InternedTag"/> plus their intern index
    /// (core-relations/src/base_values/unboxed.rs:71-105). The top bit of a value tells the two apart.
    /// </summary>
    public readonly struct UIntPtrCodec : IBaseValueCodec<nuint>
    {

        /// <inheritdoc />
        public static bool MayUnbox => true;

        /// <inheritdoc />
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool TryBox(nuint value, out Value boxed)
        {
            // Rust's `*self & (VAL_MASK - 1) == *self`: inline exactly when value < 2^31.
            if (unchecked((nuint)value) < BaseValues.InternedTag)
            {
                boxed = new Value(unchecked((uint)value));
                return true;
            }

            boxed = default;
            return false;
        }

        /// <inheritdoc />
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool TryUnbox(Value boxed, out nuint value)
        {
            // Top bit clear: an inline value, zero-extended (Rust's `val.rep() as usize`). Set: an interned one.
            if (boxed.Rep < BaseValues.InternedTag)
            {
                value = boxed.Rep;
                return true;
            }

            value = 0;
            return false;
        }

        /// <inheritdoc />
        public static string Format(nuint value) => value.ToString(CultureInfo.InvariantCulture);

    }

}

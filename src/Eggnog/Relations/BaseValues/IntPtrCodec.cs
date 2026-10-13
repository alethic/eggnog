using System.Globalization;
using System.Runtime.CompilerServices;

namespace Eggnog.Relations
{

    /// <summary>
    /// The codec for <see cref="nint"/> base values (Rust <c>isize</c>): values in <c>[0, 2^31)</c> are stored inline as
    /// themselves; all others are interned and stored as <see cref="BaseValues.InternedTag"/> plus their intern index
    /// (core-relations/src/base_values/unboxed.rs:71-105). The top bit of a value tells the two apart.
    /// </summary>
    internal readonly struct IntPtrCodec : IBaseValueCodec<nint>
    {

        /// <inheritdoc />
        public static bool MayUnbox => true;

        /// <inheritdoc />
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool TryBox(nint value, out Value boxed)
        {
            // Rust's `*self & (VAL_MASK - 1) == *self`: inline exactly when 0 <= value < 2^31. A negative value converts
            // to a huge unsigned one and fails the test.
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
        public static bool TryUnbox(Value boxed, out nint value)
        {
            // Top bit clear: an inline value, zero-extended (Rust's `val.rep() as isize`). Set: an interned one.
            if (boxed.Rep < BaseValues.InternedTag)
            {
                value = (nint)boxed.Rep;
                return true;
            }

            value = 0;
            return false;
        }

        /// <inheritdoc />
        public static string Format(nint value) => value.ToString(CultureInfo.InvariantCulture);

    }

}

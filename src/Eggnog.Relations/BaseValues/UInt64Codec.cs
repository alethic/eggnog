using System.Globalization;
using System.Runtime.CompilerServices;

namespace Eggnog.Relations
{

    /// <summary>
    /// The codec for <see cref="ulong"/> base values (Rust <c>u64</c>): values in <c>[0, 2^31)</c> are stored inline as
    /// themselves; all others are interned and stored as <see cref="BaseValues.InternedTag"/> plus their intern index
    /// (core-relations/src/base_values/unboxed.rs:71-105). The top bit of a value tells the two apart.
    /// </summary>
    internal readonly struct UInt64Codec : IBaseValueCodec<ulong>
    {

        /// <inheritdoc />
        public static bool MayUnbox => true;

        /// <inheritdoc />
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool TryBox(ulong value, out Value boxed)
        {
            // Rust's `*self & (VAL_MASK - 1) == *self`: inline exactly when value < 2^31.
            if (unchecked((ulong)value) < BaseValues.InternedTag)
            {
                boxed = new Value(unchecked((uint)value));
                return true;
            }

            boxed = default;
            return false;
        }

        /// <inheritdoc />
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool TryUnbox(Value boxed, out ulong value)
        {
            // Top bit clear: an inline value, zero-extended (Rust's `val.rep() as u64`). Set: an interned one.
            if (boxed.Rep < BaseValues.InternedTag)
            {
                value = boxed.Rep;
                return true;
            }

            value = 0;
            return false;
        }

        /// <inheritdoc />
        public static string Format(ulong value) => value.ToString(CultureInfo.InvariantCulture);

    }

}

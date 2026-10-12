using System.Globalization;
using System.Runtime.CompilerServices;

namespace Eggnog.Relations
{

    /// <summary>
    /// The codec for <see cref="byte"/> base values (Rust <c>u8</c>): always inline, zero-extended to 32 bits, and read
    /// back by truncation (core-relations/src/base_values/unboxed.rs:12-31). Never touches an intern table.
    /// </summary>
    internal readonly struct ByteCodec : IBaseValueCodec<byte>
    {

        /// <inheritdoc />
        public static bool MayUnbox => true;

        /// <inheritdoc />
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool TryBox(byte value, out Value boxed)
        {
            // Rust's `*self as u32`.
            boxed = new Value(unchecked((uint)value));
            return true;
        }

        /// <inheritdoc />
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool TryUnbox(Value boxed, out byte value)
        {
            // Rust's `val.rep() as u8`.
            value = unchecked((byte)boxed.Rep);
            return true;
        }

        /// <inheritdoc />
        public static string Format(byte value) => value.ToString(CultureInfo.InvariantCulture);

    }

}

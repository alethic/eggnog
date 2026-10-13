using System.Globalization;
using System.Runtime.CompilerServices;

namespace Eggnog.Relations
{

    /// <summary>
    /// The codec for <see cref="short"/> base values (Rust <c>i16</c>): always inline, sign-extended to 32 bits, and read
    /// back by truncation (core-relations/src/base_values/unboxed.rs:12-31). Never touches an intern table. <c>-1</c> boxes
    /// to <see cref="Value.StaleRep"/>, so it must not be stored in a table's first column.
    /// </summary>
    internal readonly struct Int16Codec : IBaseValueCodec<short>
    {

        /// <inheritdoc />
        public static bool MayUnbox => true;

        /// <inheritdoc />
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool TryBox(short value, out Value boxed)
        {
            // Rust's `*self as u32`.
            boxed = new Value(unchecked((uint)value));
            return true;
        }

        /// <inheritdoc />
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool TryUnbox(Value boxed, out short value)
        {
            // Rust's `val.rep() as i16`.
            value = unchecked((short)boxed.Rep);
            return true;
        }

        /// <inheritdoc />
        public static string Format(short value) => value.ToString(CultureInfo.InvariantCulture);

    }

}

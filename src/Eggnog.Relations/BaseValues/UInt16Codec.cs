using System.Globalization;
using System.Runtime.CompilerServices;

namespace Eggnog.Relations
{

    /// <summary>
    /// The codec for <see cref="ushort"/> base values (Rust <c>u16</c>): always inline, zero-extended to 32 bits, and
    /// read back by truncation (core-relations/src/base_values/unboxed.rs:12-31). Never touches an intern table.
    /// </summary>
    public readonly struct UInt16Codec : IBaseValueCodec<ushort>
    {

        /// <inheritdoc />
        public static bool MayUnbox => true;

        /// <inheritdoc />
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool TryBox(ushort value, out Value boxed)
        {
            // Rust's `*self as u32`.
            boxed = new Value(unchecked((uint)value));
            return true;
        }

        /// <inheritdoc />
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool TryUnbox(Value boxed, out ushort value)
        {
            // Rust's `val.rep() as u16`.
            value = unchecked((ushort)boxed.Rep);
            return true;
        }

        /// <inheritdoc />
        public static string Format(ushort value) => value.ToString(CultureInfo.InvariantCulture);

    }

}

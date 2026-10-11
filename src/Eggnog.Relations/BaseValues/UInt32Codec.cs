using System.Globalization;
using System.Runtime.CompilerServices;

namespace Eggnog.Relations
{

    /// <summary>
    /// The codec for <see cref="uint"/> base values (Rust <c>u32</c>): always inline, as the value itself
    /// (core-relations/src/base_values/unboxed.rs:12-31). Never touches an intern table. <see cref="uint.MaxValue"/>
    /// boxes to <see cref="Value.StaleRep"/>, so it must not be stored in a table's first column.
    /// </summary>
    public readonly struct UInt32Codec : IBaseValueCodec<uint>
    {

        /// <inheritdoc />
        public static bool MayUnbox => true;

        /// <inheritdoc />
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool TryBox(uint value, out Value boxed)
        {
            // Rust's `*self as u32`.
            boxed = new Value(unchecked((uint)value));
            return true;
        }

        /// <inheritdoc />
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool TryUnbox(Value boxed, out uint value)
        {
            // Rust's `val.rep() as u32`.
            value = unchecked((uint)boxed.Rep);
            return true;
        }

        /// <inheritdoc />
        public static string Format(uint value) => value.ToString(CultureInfo.InvariantCulture);

    }

}

using System.Globalization;
using System.Runtime.CompilerServices;

namespace Eggnog.Relations
{

    /// <summary>
    /// The codec for <see cref="sbyte"/> base values (Rust <c>i8</c>): always inline, sign-extended to 32 bits, and read
    /// back by truncation (core-relations/src/base_values/unboxed.rs:12-31). Never touches an intern table. <c>-1</c> boxes
    /// to <see cref="Value.StaleRep"/>, so it must not be stored in a table's first column.
    /// </summary>
    public readonly struct SByteCodec : IBaseValueCodec<sbyte>
    {

        /// <inheritdoc />
        public static bool MayUnbox => true;

        /// <inheritdoc />
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool TryBox(sbyte value, out Value boxed)
        {
            // Rust's `*self as u32`.
            boxed = new Value(unchecked((uint)value));
            return true;
        }

        /// <inheritdoc />
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool TryUnbox(Value boxed, out sbyte value)
        {
            // Rust's `val.rep() as i8`.
            value = unchecked((sbyte)boxed.Rep);
            return true;
        }

        /// <inheritdoc />
        public static string Format(sbyte value) => value.ToString(CultureInfo.InvariantCulture);

    }

}

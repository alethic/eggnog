using System.Globalization;
using System.Runtime.CompilerServices;

namespace Eggnog.Relations
{

    /// <summary>
    /// The codec for <see cref="int"/> base values (Rust <c>i32</c>): always inline, as the value's bits
    /// (core-relations/src/base_values/unboxed.rs:12-31). Never touches an intern table. <c>-1</c> boxes to
    /// <see cref="Value.StaleRep"/>, so it must not be stored in a table's first column.
    /// </summary>
    internal readonly struct Int32Codec : IBaseValueCodec<int>
    {

        /// <inheritdoc />
        public static bool MayUnbox => true;

        /// <inheritdoc />
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool TryBox(int value, out Value boxed)
        {
            // Rust's `*self as u32`.
            boxed = new Value(unchecked((uint)value));
            return true;
        }

        /// <inheritdoc />
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool TryUnbox(Value boxed, out int value)
        {
            // Rust's `val.rep() as i32`.
            value = unchecked((int)boxed.Rep);
            return true;
        }

        /// <inheritdoc />
        public static string Format(int value) => value.ToString(CultureInfo.InvariantCulture);

    }

}

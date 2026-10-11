using System.Runtime.CompilerServices;

namespace Eggnog.Relations
{

    /// <summary>
    /// The codec for <see cref="bool"/> base values: always inline, <c>true</c> as 1 and <c>false</c> as 0; any nonzero
    /// value reads back as <c>true</c> (core-relations/src/base_values/unboxed.rs:33-52). Never touches an intern table.
    /// egglog stores a table's <c>subsumed</c> column with this encoding.
    /// </summary>
    public readonly struct BooleanCodec : IBaseValueCodec<bool>
    {

        /// <inheritdoc />
        public static bool MayUnbox => true;

        /// <inheritdoc />
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool TryBox(bool value, out Value boxed)
        {
            boxed = new Value(value ? 1u : 0u);
            return true;
        }

        /// <inheritdoc />
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool TryUnbox(Value boxed, out bool value)
        {
            value = boxed.Rep != 0;
            return true;
        }

        /// <summary>
        /// Formats the value as Rust's <c>Debug</c> does: <c>true</c> or <c>false</c>.
        /// </summary>
        /// <param name="value">The value.</param>
        /// <returns>The text.</returns>
        public static string Format(bool value) => value ? "true" : "false";

    }

}

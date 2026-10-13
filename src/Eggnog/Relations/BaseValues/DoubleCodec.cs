using System.Runtime.CompilerServices;

namespace Eggnog.Relations
{

    /// <summary>
    /// The codec for <see cref="double"/> base values, the egglog frontend's <c>F = Boxed&lt;OrderedFloat&lt;f64&gt;&gt;</c>
    /// (src/sort/mod.rs:19): never inline (egglog never unboxes floats); every float is interned and stored as its
    /// intern index.
    /// </summary>
    /// <remarks>
    /// Equality is <see cref="double.Equals(double)"/>, which is <c>OrderedFloat</c>'s: every NaN equals every other NaN
    /// and <c>-0.0</c> equals <c>0.0</c>, and <see cref="InternTable{TKey}"/> hashes them alike. So all NaNs share one
    /// value, and so do both zeros: whichever was interned first is the one <see cref="BaseValues.Unwrap{TCodec, T}"/>
    /// returns, as in egglog.
    /// </remarks>
    internal readonly struct DoubleCodec : IBaseValueCodec<double>
    {

        /// <inheritdoc />
        public static bool MayUnbox => false;

        /// <inheritdoc />
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool TryBox(double value, out Value boxed)
        {
            boxed = default;
            return false;
        }

        /// <inheritdoc />
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool TryUnbox(Value boxed, out double value)
        {
            value = 0;
            return false;
        }

        /// <summary>
        /// Formats the value exactly as Rust's <c>Debug</c> for <c>f64</c> (and so <c>OrderedFloat&lt;f64&gt;</c>) does:
        /// the shortest digits that round-trip, a tie between two equally near candidates rounding up in magnitude, in
        /// exponential form (<c>1e16</c>, <c>1.5e-7</c>) when the magnitude is below <c>1e-4</c> or at least <c>1e16</c>,
        /// otherwise in decimal form with at least one fractional digit (<c>1.0</c>, <c>0.0001</c>); <c>NaN</c>,
        /// <c>inf</c> and <c>-inf</c> for the non-finite values; and a sign on negative zero. See
        /// <see cref="RustFloatDebug"/>.
        /// </summary>
        /// <param name="value">The value.</param>
        /// <returns>The text.</returns>
        public static string Format(double value) => RustFloatDebug.Format(value);

    }

}

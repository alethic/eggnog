using System.Diagnostics.CodeAnalysis;

namespace Eggnog
{

    /// <summary>
    /// The engine codec of <see cref="BigRational"/>: every value interned, as egglog's <c>Q = Boxed&lt;BigRational&gt;</c>
    /// is (<see cref="Relations.InternedCodec{T}"/>), and formatted as Rust's derived <c>Debug</c> of num-rational's
    /// <c>Ratio</c>, which <c>Boxed</c> forwards to and egglog's serializer prints: <c>Ratio { numer: 3, denom: 4 }</c>.
    /// </summary>
    internal readonly struct BigRationalCodec : Relations.IBaseValueCodec<BigRational>
    {

        /// <inheritdoc />
        public static bool MayUnbox => false;

        /// <inheritdoc />
        public static bool TryBox(BigRational value, out Relations.Value boxed)
        {
            boxed = default;
            return false;
        }

        /// <inheritdoc />
        public static bool TryUnbox(Relations.Value boxed, [MaybeNullWhen(false)] out BigRational value)
        {
            value = default;
            return false;
        }

        /// <inheritdoc />
        public static string Format(BigRational value) => value.FormatDebug();

    }

}

using System;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;

namespace Eggnog.Relations
{

    /// <summary>
    /// The codec that interns every value of an equatable type <typeparamref name="T"/> and stores it as its intern index:
    /// the port of egglog's <c>Boxed&lt;T&gt;</c> newtype and of the trait's default methods
    /// (core-relations/src/base_values/mod.rs:29-46, 190-216), which make any hashable type a base value. The egglog
    /// frontend uses it for big integers (<c>Z = Boxed&lt;BigInt&gt;</c>, here
    /// <c>InternedCodec&lt;System.Numerics.BigInteger&gt;</c>) and big rationals.
    /// </summary>
    /// <remarks>
    /// Equality is <see cref="IEquatable{T}"/> and the hash is <see cref="object.GetHashCode"/> (see
    /// <see cref="InternTable{TKey}"/>), which must agree. Rust's <c>Boxed</c> also forwards arithmetic operators to the
    /// wrapped value; here the base value is the .NET value itself, so there is nothing to forward. Each distinct
    /// <typeparamref name="T"/> is its own base value type; to intern one .NET type as two base value types, write a
    /// second codec.
    /// </remarks>
    /// <typeparam name="T">The base value type.</typeparam>
    public readonly struct InternedCodec<T> : IBaseValueCodec<T>
        where T : notnull, IEquatable<T>
    {

        /// <inheritdoc />
        public static bool MayUnbox => false;

        /// <inheritdoc />
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool TryBox(T value, out Value boxed)
        {
            boxed = default;
            return false;
        }

        /// <inheritdoc />
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool TryUnbox(Value boxed, [MaybeNullWhen(false)] out T value)
        {
            value = default;
            return false;
        }

        /// <summary>
        /// Formats the value as Rust's <c>Debug</c> for the wrapped type does, which <c>Boxed&lt;T&gt;</c> forwards to
        /// (mod.rs:209-213). The types with a Rust counterpart print exactly as Rust prints that counterpart:
        /// <see cref="string"/> as <c>str</c> (quoted and escaped, see <see cref="StringCodec.Format"/>),
        /// <see cref="double"/> and <see cref="float"/> as <c>f64</c> and <c>f32</c> (see
        /// <see cref="DoubleCodec.Format"/>), <see cref="bool"/> as <c>true</c>/<c>false</c>, <see cref="char"/> and
        /// <see cref="Rune"/> as <c>char</c> (single-quoted and escaped), <see cref="ValueTuple"/> as <c>()</c>, and the
        /// integer types and <see cref="System.Numerics.BigInteger"/> (Rust's <c>BigInt</c>) in decimal. Any other type
        /// has no Rust <c>Debug</c> to match; it formats with the invariant culture if it is <see cref="IFormattable"/>
        /// and with <see cref="object.ToString"/> otherwise. Cold path.
        /// </summary>
        /// <param name="value">The value.</param>
        /// <returns>The text.</returns>
        public static string Format(T value)
        {
            // The type tests are JIT-time constants for value types; for strings they are one type-handle compare.
            if (typeof(T) == typeof(string))
                return RustStrDebug.Format((string)(object)value);

            if (typeof(T) == typeof(double))
                return RustFloatDebug.Format((double)(object)value);

            if (typeof(T) == typeof(float))
                return RustFloatDebug.Format((float)(object)value);

            if (typeof(T) == typeof(bool))
                return (bool)(object)value ? "true" : "false";

            if (typeof(T) == typeof(char))
                return RustStrDebug.Format((char)(object)value);

            if (typeof(T) == typeof(Rune))
                return RustStrDebug.Format((Rune)(object)value);

            if (typeof(T) == typeof(ValueTuple))
                return "()";

            if (value is IFormattable formattable)
                return formattable.ToString(null, CultureInfo.InvariantCulture);

            return value.ToString() ?? string.Empty;
        }

    }

}

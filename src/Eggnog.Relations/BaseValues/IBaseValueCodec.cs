using System;
using System.Diagnostics.CodeAnalysis;

namespace Eggnog.Relations
{

    /// <summary>
    /// Describes how values of a base value type <typeparamref name="T"/> are stored in a 32-bit <see cref="Value"/>:
    /// the port of egglog's <c>BaseValue</c> trait (core-relations/src/base_values/mod.rs:29-46).
    /// </summary>
    /// <remarks>
    /// <para>
    /// A codec is an empty struct whose members are all static, so that <see cref="BaseValues"/>, which takes the codec as
    /// a struct type parameter, is specialized for each codec by the JIT and the inline path compiles to a compare and a
    /// cast, as Rust's monomorphized <c>BaseValues::get::&lt;P&gt;</c> does. The codec type, not
    /// <typeparamref name="T"/>, identifies a base value type: two codecs over the same .NET type are two base value
    /// types with separate intern tables, as two Rust newtypes over one type are.
    /// </para>
    /// <para>
    /// A value either fits in a <see cref="Value"/> (<see cref="TryBox"/> succeeds, and no intern table is touched) or is
    /// interned. egglog's encoding, which this contract preserves, is:
    /// </para>
    /// <list type="bullet">
    /// <item>A codec whose <see cref="MayUnbox"/> is <c>false</c> never boxes inline: every value is interned and its
    /// <see cref="Value"/> is the intern index (from 0).</item>
    /// <item>A codec whose <see cref="MayUnbox"/> is <c>true</c> and whose <see cref="TryBox"/> succeeds for every value
    /// (small integers, <see cref="bool"/>, unit) may use all 32 bits.</item>
    /// <item>A codec whose <see cref="MayUnbox"/> is <c>true</c> and whose <see cref="TryBox"/> fails for some values (the
    /// 64-bit integers) must box only into <c>[0, 2^31)</c>, and its <see cref="TryUnbox"/> must fail exactly when the top
    /// bit is set: interned values are stored as <see cref="BaseValues.InternedTag"/> plus the intern index.</item>
    /// </list>
    /// <para>
    /// Every codec must be canonical: equal values (by <see cref="IEquatable{T}"/>) box to the same <see cref="Value"/>
    /// and distinct values to distinct ones, so that comparing raw values is comparing base values. <see cref="Value"/>
    /// order is not the order of <typeparamref name="T"/>.
    /// </para>
    /// <para>
    /// egglog's trait also has <c>intern</c> and <c>as_any</c> hooks. Neither is ported: <c>BaseInternTable</c> never calls
    /// <c>intern</c> (it interns directly, mod.rs:158-176) and <c>as_any</c> is Rust's downcasting plumbing.
    /// </para>
    /// </remarks>
    /// <typeparam name="T">The base value type. Equality is <see cref="IEquatable{T}"/>, as the intern table uses.</typeparam>
    public interface IBaseValueCodec<T>
        where T : notnull, IEquatable<T>
    {

        /// <summary>
        /// Gets whether <see cref="TryBox"/> can ever succeed: egglog's <c>MAY_UNBOX</c>. A JIT-time constant for each
        /// codec, so that the inline path vanishes for interned-only types.
        /// </summary>
        static abstract bool MayUnbox { get; }

        /// <summary>
        /// Stores <paramref name="value"/> directly in a <see cref="Value"/>, if it fits: egglog's <c>try_box</c>.
        /// </summary>
        /// <param name="value">The base value.</param>
        /// <param name="boxed">The value it is stored as, when this returns <c>true</c>.</param>
        /// <returns>Whether the value fits; if not, it is interned.</returns>
        static abstract bool TryBox(T value, out Value boxed);

        /// <summary>
        /// Reads a base value stored directly in <paramref name="boxed"/>: egglog's <c>try_unbox</c>.
        /// </summary>
        /// <param name="boxed">The stored value.</param>
        /// <param name="value">The base value, when this returns <c>true</c>.</param>
        /// <returns>Whether <paramref name="boxed"/> holds the value inline; if not, it is an interned value.</returns>
        static abstract bool TryUnbox(Value boxed, [MaybeNullWhen(false)] out T value);

        /// <summary>
        /// Formats a base value for diagnostics, as Rust's <c>Debug</c> formatting does for egglog's
        /// <c>BaseValuePrinter</c> (mod.rs:56-64).
        /// </summary>
        /// <param name="value">The base value.</param>
        /// <returns>The text.</returns>
        static abstract string Format(T value);

    }

}

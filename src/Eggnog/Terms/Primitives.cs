using System;

namespace Eggnog
{

    /// <summary>
    /// Named built-in primitives, with egglog's names, made once per element type. Arithmetic is checked and partial, as
    /// egglog's is: an overflow is no result, which drops the match in a rule body and fails the rule in a head (D31).
    /// </summary>
    public static class Primitives
    {

        /// <summary>
        /// <c>set-of</c>: the singleton set of a value.
        /// </summary>
        /// <typeparam name="T">The element type.</typeparam>
        /// <returns>The primitive.</returns>
        public static Primitive<T, ValueSet<T>> Singleton<T>() where T : notnull => PrimitiveCache<T>.Singleton;

        /// <summary>
        /// <c>set-union</c>.
        /// </summary>
        /// <typeparam name="T">The element type.</typeparam>
        /// <returns>The primitive.</returns>
        public static Primitive<ValueSet<T>, ValueSet<T>, ValueSet<T>> Union<T>() where T : notnull => PrimitiveCache<T>.Union;

        /// <summary>
        /// <c>set-intersect</c>.
        /// </summary>
        /// <typeparam name="T">The element type.</typeparam>
        /// <returns>The primitive.</returns>
        public static Primitive<ValueSet<T>, ValueSet<T>, ValueSet<T>> Intersect<T>() where T : notnull => PrimitiveCache<T>.Intersect;

        /// <summary>
        /// <c>set-diff</c>: the elements of the first set not in the second.
        /// </summary>
        /// <typeparam name="T">The element type.</typeparam>
        /// <returns>The primitive.</returns>
        public static Primitive<ValueSet<T>, ValueSet<T>, ValueSet<T>> Except<T>() where T : notnull => PrimitiveCache<T>.Except;

        /// <summary>
        /// <c>set-insert</c>.
        /// </summary>
        /// <typeparam name="T">The element type.</typeparam>
        /// <returns>The primitive.</returns>
        public static Primitive<ValueSet<T>, T, ValueSet<T>> Insert<T>() where T : notnull => PrimitiveCache<T>.Insert;

        /// <summary>
        /// <c>set-remove</c>.
        /// </summary>
        /// <typeparam name="T">The element type.</typeparam>
        /// <returns>The primitive.</returns>
        public static Primitive<ValueSet<T>, T, ValueSet<T>> Remove<T>() where T : notnull => PrimitiveCache<T>.Remove;

        /// <summary>
        /// <c>set-contains</c>.
        /// </summary>
        /// <typeparam name="T">The element type.</typeparam>
        /// <returns>The primitive.</returns>
        public static Primitive<ValueSet<T>, T, bool> Contains<T>() where T : notnull => PrimitiveCache<T>.Contains;

        /// <summary>
        /// <c>set-subset</c>: whether the first set is a subset of the second.
        /// </summary>
        /// <typeparam name="T">The element type.</typeparam>
        /// <returns>The primitive.</returns>
        public static Primitive<ValueSet<T>, ValueSet<T>, bool> IsSubsetOf<T>() where T : notnull => PrimitiveCache<T>.IsSubsetOf;

        /// <summary>
        /// <c>set-empty</c>: whether the set is empty.
        /// </summary>
        /// <typeparam name="T">The element type.</typeparam>
        /// <returns>The primitive.</returns>
        public static Primitive<ValueSet<T>, bool> IsEmpty<T>() where T : notnull => PrimitiveCache<T>.IsEmpty;

        /// <summary>
        /// <c>set-length</c>.
        /// </summary>
        /// <typeparam name="T">The element type.</typeparam>
        /// <returns>The primitive.</returns>
        public static Primitive<ValueSet<T>, long> Count<T>() where T : notnull => PrimitiveCache<T>.Count;

        /// <summary>
        /// Gets <c>+</c> over <c>i64</c>: partial, like egglog's (<c>checked_add</c>). An overflow has no result: in a
        /// rule body the match is dropped; in a head the rule fails (D31). <see cref="Primitive{T1, T2, TResult}.Function"/>
        /// throws <see cref="OverflowException"/>.
        /// </summary>
        public static Primitive<long, long, long> Add { get; } = new("+", static (a, b) => checked(a + b), false) { IsPartial = true };

        /// <summary>
        /// Gets <c>-</c> over <c>i64</c>: partial, like egglog's (<c>checked_sub</c>). An overflow has no result: in a
        /// rule body the match is dropped; in a head the rule fails (D31). <see cref="Primitive{T1, T2, TResult}.Function"/>
        /// throws <see cref="OverflowException"/>.
        /// </summary>
        public static Primitive<long, long, long> Subtract { get; } = new("-", static (a, b) => checked(a - b), false) { IsPartial = true };

        /// <summary>
        /// Gets <c>max</c> over <c>i64</c>.
        /// </summary>
        public static Primitive<long, long, long> Max { get; } = new("max", static (a, b) => Math.Max(a, b), false);

        /// <summary>
        /// Gets <c>min</c> over <c>i64</c>.
        /// </summary>
        public static Primitive<long, long, long> Min { get; } = new("min", static (a, b) => Math.Min(a, b), false);

        /// <summary>
        /// Gets <c>&lt;</c> over <c>i64</c>.
        /// </summary>
        public static Primitive<long, long, bool> Less { get; } = new("<", static (a, b) => a < b, false);

        /// <summary>
        /// Gets <c>&lt;=</c> over <c>i64</c>.
        /// </summary>
        public static Primitive<long, long, bool> LessOrEqual { get; } = new("<=", static (a, b) => a <= b, false);

        /// <summary>
        /// Gets <c>&gt;</c> over <c>i64</c>.
        /// </summary>
        public static Primitive<long, long, bool> Greater { get; } = new(">", static (a, b) => a > b, false);

        /// <summary>
        /// Gets <c>&gt;=</c> over <c>i64</c>.
        /// </summary>
        public static Primitive<long, long, bool> GreaterOrEqual { get; } = new(">=", static (a, b) => a >= b, false);

        /// <summary>
        /// Gets the internal <c>is-true</c> filter of a boolean term that is not a computation (§6.3, step 3).
        /// </summary>
        internal static Primitive<bool, bool> IsTrue { get; } = new("is-true", static b => b, false);

    }

}

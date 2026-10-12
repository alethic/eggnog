using System;

namespace Eggnog
{

    /// <summary>
    /// Computed values from C# lambdas, which render as <c>&lt;lambda&gt;</c>; prefer named primitives
    /// (<see cref="Primitive.Create{T1, TResult}(string, Func{T1, TResult})"/>, <see cref="Primitives"/>) where rendering
    /// matters. Lambdas must be pure, deterministic and thread-safe, and must not call into any graph.
    /// </summary>
    public static class Compute
    {

        /// <summary>
        /// A lambda of one argument.
        /// </summary>
        /// <typeparam name="T1">The argument type.</typeparam>
        /// <typeparam name="TResult">The result type.</typeparam>
        /// <param name="a">The argument.</param>
        /// <param name="function">The lambda.</param>
        /// <returns>The computed term.</returns>
        public static Term<TResult> Of<T1, TResult>(Term<T1> a, Func<T1, TResult> function)
        {
            ArgumentNullException.ThrowIfNull(function);
            return new Primitive<T1, TResult>(Primitive.LambdaName, function, true).Of(a);
        }

        /// <summary>
        /// A lambda of two arguments.
        /// </summary>
        /// <typeparam name="T1">The first argument type.</typeparam>
        /// <typeparam name="T2">The second argument type.</typeparam>
        /// <typeparam name="TResult">The result type.</typeparam>
        /// <param name="a">The first argument.</param>
        /// <param name="b">The second argument.</param>
        /// <param name="function">The lambda.</param>
        /// <returns>The computed term.</returns>
        public static Term<TResult> Of<T1, T2, TResult>(Term<T1> a, Term<T2> b, Func<T1, T2, TResult> function)
        {
            ArgumentNullException.ThrowIfNull(function);
            return new Primitive<T1, T2, TResult>(Primitive.LambdaName, function, true).Of(a, b);
        }

        /// <summary>
        /// A lambda of three arguments.
        /// </summary>
        /// <typeparam name="T1">The first argument type.</typeparam>
        /// <typeparam name="T2">The second argument type.</typeparam>
        /// <typeparam name="T3">The third argument type.</typeparam>
        /// <typeparam name="TResult">The result type.</typeparam>
        /// <param name="a">The first argument.</param>
        /// <param name="b">The second argument.</param>
        /// <param name="c">The third argument.</param>
        /// <param name="function">The lambda.</param>
        /// <returns>The computed term.</returns>
        public static Term<TResult> Of<T1, T2, T3, TResult>(Term<T1> a, Term<T2> b, Term<T3> c, Func<T1, T2, T3, TResult> function)
        {
            ArgumentNullException.ThrowIfNull(function);
            return new Primitive<T1, T2, T3, TResult>(Primitive.LambdaName, function, true).Of(a, b, c);
        }

        /// <summary>
        /// A lambda of four arguments.
        /// </summary>
        /// <typeparam name="T1">The first argument type.</typeparam>
        /// <typeparam name="T2">The second argument type.</typeparam>
        /// <typeparam name="T3">The third argument type.</typeparam>
        /// <typeparam name="T4">The fourth argument type.</typeparam>
        /// <typeparam name="TResult">The result type.</typeparam>
        /// <param name="a">The first argument.</param>
        /// <param name="b">The second argument.</param>
        /// <param name="c">The third argument.</param>
        /// <param name="d">The fourth argument.</param>
        /// <param name="function">The lambda.</param>
        /// <returns>The computed term.</returns>
        public static Term<TResult> Of<T1, T2, T3, T4, TResult>(Term<T1> a, Term<T2> b, Term<T3> c, Term<T4> d, Func<T1, T2, T3, T4, TResult> function)
        {
            ArgumentNullException.ThrowIfNull(function);
            return new Primitive<T1, T2, T3, T4, TResult>(Primitive.LambdaName, function, true).Of(a, b, c, d);
        }

        /// <summary>
        /// The untyped application of a primitive (D13): one term per parameter, of the parameter's CLR type.
        /// </summary>
        /// <param name="primitive">The primitive.</param>
        /// <param name="arguments">The arguments.</param>
        /// <returns>The computed term.</returns>
        /// <exception cref="ArgumentException">The count or a type does not match.</exception>
        internal static Term Of(Primitive primitive, params Term[] arguments)
        {
            ArgumentNullException.ThrowIfNull(primitive);
            ArgumentNullException.ThrowIfNull(arguments);
            if (arguments.Length != primitive.ParameterTypes.Count)
                throw new ArgumentException($"'{primitive.Name}' takes {primitive.ParameterTypes.Count} arguments, not {arguments.Length}.", nameof(arguments));

            for (var i = 0; i < arguments.Length; i++)
            {
                var argument = arguments[i] ?? throw new ArgumentNullException(nameof(arguments), $"Argument {i} of '{primitive.Name}' is null.");
                if (argument.ClrType != primitive.ParameterTypes[i])
                    throw new ArgumentException($"Argument {i} of '{primitive.Name}' is a {ValueStore.DisplayName(argument.ClrType)}, not a {ValueStore.DisplayName(primitive.ParameterTypes[i])}.", nameof(arguments));
            }

            return new UntypedComputeTerm(primitive, (Term[])arguments.Clone());
        }

    }

}

using System;

namespace Eggnog
{

    /// <summary>
    /// Makes the built-in primitives of <see cref="Primitives"/>, <see cref="DoublePrimitives"/>,
    /// <see cref="BigIntegerPrimitives"/>, <see cref="BooleanPrimitives"/> and <see cref="StringPrimitives"/>: total ones
    /// over a function, and partial ones (egglog's <c>-?&gt;</c>) over a partial function, whose <c>Function</c> throws
    /// where there is no result: the given function, which throws what .NET's own operation throws, or else
    /// <see cref="InvalidOperationException"/>.
    /// </summary>
    internal static class BuiltInPrimitive
    {

        /// <summary>
        /// A total primitive of one parameter.
        /// </summary>
        /// <typeparam name="T1">The parameter type.</typeparam>
        /// <typeparam name="TResult">The result type.</typeparam>
        /// <param name="name">The egglog name.</param>
        /// <param name="function">The function.</param>
        /// <returns>The primitive.</returns>
        public static Primitive<T1, TResult> Total<T1, TResult>(string name, Func<T1, TResult> function) => new(name, function, false);

        /// <summary>
        /// A total primitive of two parameters.
        /// </summary>
        /// <typeparam name="T1">The first parameter type.</typeparam>
        /// <typeparam name="T2">The second parameter type.</typeparam>
        /// <typeparam name="TResult">The result type.</typeparam>
        /// <param name="name">The egglog name.</param>
        /// <param name="function">The function.</param>
        /// <returns>The primitive.</returns>
        public static Primitive<T1, T2, TResult> Total<T1, T2, TResult>(string name, Func<T1, T2, TResult> function) => new(name, function, false);

        /// <summary>
        /// A total primitive of three parameters.
        /// </summary>
        /// <typeparam name="T1">The first parameter type.</typeparam>
        /// <typeparam name="T2">The second parameter type.</typeparam>
        /// <typeparam name="T3">The third parameter type.</typeparam>
        /// <typeparam name="TResult">The result type.</typeparam>
        /// <param name="name">The egglog name.</param>
        /// <param name="function">The function.</param>
        /// <returns>The primitive.</returns>
        public static Primitive<T1, T2, T3, TResult> Total<T1, T2, T3, TResult>(string name, Func<T1, T2, T3, TResult> function) =>
            new(name, function, false);

        /// <summary>
        /// A partial primitive of one parameter.
        /// </summary>
        /// <typeparam name="T1">The parameter type.</typeparam>
        /// <typeparam name="TResult">The result type.</typeparam>
        /// <param name="name">The egglog name.</param>
        /// <param name="function">The partial function.</param>
        /// <returns>The primitive.</returns>
        public static Primitive<T1, TResult> Partial<T1, TResult>(string name, PartialFunc<T1, TResult> function) =>
            new(name, a => function(a, out var result) ? result : throw Primitive.NoResult(name), function);

        /// <summary>
        /// A partial primitive of two parameters.
        /// </summary>
        /// <typeparam name="T1">The first parameter type.</typeparam>
        /// <typeparam name="T2">The second parameter type.</typeparam>
        /// <typeparam name="TResult">The result type.</typeparam>
        /// <param name="name">The egglog name.</param>
        /// <param name="function">The partial function.</param>
        /// <returns>The primitive.</returns>
        public static Primitive<T1, T2, TResult> Partial<T1, T2, TResult>(string name, PartialFunc<T1, T2, TResult> function) =>
            new(name, (a, b) => function(a, b, out var result) ? result : throw Primitive.NoResult(name), function);

        /// <summary>
        /// A partial primitive of one parameter whose function throws its own exception where there is no result.
        /// </summary>
        /// <typeparam name="T1">The parameter type.</typeparam>
        /// <typeparam name="TResult">The result type.</typeparam>
        /// <param name="name">The egglog name.</param>
        /// <param name="function">The function, which throws where there is no result.</param>
        /// <param name="partial">The partial function.</param>
        /// <returns>The primitive.</returns>
        public static Primitive<T1, TResult> Partial<T1, TResult>(string name, Func<T1, TResult> function, PartialFunc<T1, TResult> partial) =>
            new(name, function, partial);

        /// <summary>
        /// A partial primitive of two parameters whose function throws its own exception where there is no result.
        /// </summary>
        /// <typeparam name="T1">The first parameter type.</typeparam>
        /// <typeparam name="T2">The second parameter type.</typeparam>
        /// <typeparam name="TResult">The result type.</typeparam>
        /// <param name="name">The egglog name.</param>
        /// <param name="function">The function, which throws where there is no result.</param>
        /// <param name="partial">The partial function.</param>
        /// <returns>The primitive.</returns>
        public static Primitive<T1, T2, TResult> Partial<T1, T2, TResult>(string name, Func<T1, T2, TResult> function,
            PartialFunc<T1, T2, TResult> partial) =>
            new(name, function, partial);

    }

}

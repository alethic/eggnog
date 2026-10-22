using System;

namespace Eggnog
{

    /// <summary>
    /// Function values on the host (<see cref="UnstableFn{T1, TResult}"/> and its arities): egglog's <c>unstable-fn</c>
    /// (src/sort/fn.rs), made from the function itself rather than its name.
    /// </summary>
    public static class UnstableFn
    {

        /// <summary>
        /// Makes a function value on the host, as egglog's <c>unstable-fn</c> makes one: <paramref name="target"/> applied
        /// to <paramref name="arguments"/>, its first parameters, then to the inputs.
        /// </summary>
        /// <typeparam name="TResult">The type of the output, the target's result.</typeparam>
        /// <param name="target">A constructor or a function.</param>
        /// <param name="arguments">The partially applied arguments, one per parameter before the inputs.</param>
        /// <returns>The value.</returns>
        /// <exception cref="ArgumentException">The target is a relation, its parameters are not the arguments' then the
        /// inputs, its result is not the output, or an argument is not of its parameter's type.</exception>
        public static UnstableFn<TResult> Of<TResult>(TableHandle target, params ReadOnlySpan<object> arguments)
            where TResult : notnull
            => new(FnValue.Create(target, arguments, [], typeof(TResult)));

        /// <summary>
        /// Makes a function value on the host, as egglog's <c>unstable-fn</c> makes one: <paramref name="target"/> applied
        /// to <paramref name="arguments"/>, its first parameters, then to the inputs.
        /// </summary>
        /// <typeparam name="TResult">The type of the output, the target's result.</typeparam>
        /// <param name="target">A primitive.</param>
        /// <param name="arguments">The partially applied arguments, one per parameter before the inputs.</param>
        /// <returns>The value.</returns>
        /// <exception cref="ArgumentException">The target's parameters are not the arguments' then the inputs, its result
        /// is not the output, or an argument is not of its parameter's type.</exception>
        public static UnstableFn<TResult> Of<TResult>(Primitive target, params ReadOnlySpan<object> arguments)
            where TResult : notnull
            => new(FnValue.Create(target, arguments, [], typeof(TResult)));

        /// <summary>
        /// Makes a function value on the host, as egglog's <c>unstable-fn</c> makes one: <paramref name="target"/> applied
        /// to <paramref name="arguments"/>, its first parameters, then to the inputs.
        /// </summary>
        /// <typeparam name="T1">The type of input 1.</typeparam>
        /// <typeparam name="TResult">The type of the output, the target's result.</typeparam>
        /// <param name="target">A constructor or a function.</param>
        /// <param name="arguments">The partially applied arguments, one per parameter before the inputs.</param>
        /// <returns>The value.</returns>
        /// <exception cref="ArgumentException">The target is a relation, its parameters are not the arguments' then the
        /// inputs, its result is not the output, or an argument is not of its parameter's type.</exception>
        public static UnstableFn<T1, TResult> Of<T1, TResult>(TableHandle target, params ReadOnlySpan<object> arguments)
            where T1 : notnull
            where TResult : notnull
            => new(FnValue.Create(target, arguments, [typeof(T1)], typeof(TResult)));

        /// <summary>
        /// Makes a function value on the host, as egglog's <c>unstable-fn</c> makes one: <paramref name="target"/> applied
        /// to <paramref name="arguments"/>, its first parameters, then to the inputs.
        /// </summary>
        /// <typeparam name="T1">The type of input 1.</typeparam>
        /// <typeparam name="TResult">The type of the output, the target's result.</typeparam>
        /// <param name="target">A primitive.</param>
        /// <param name="arguments">The partially applied arguments, one per parameter before the inputs.</param>
        /// <returns>The value.</returns>
        /// <exception cref="ArgumentException">The target's parameters are not the arguments' then the inputs, its result
        /// is not the output, or an argument is not of its parameter's type.</exception>
        public static UnstableFn<T1, TResult> Of<T1, TResult>(Primitive target, params ReadOnlySpan<object> arguments)
            where T1 : notnull
            where TResult : notnull
            => new(FnValue.Create(target, arguments, [typeof(T1)], typeof(TResult)));

        /// <summary>
        /// Makes a function value on the host, as egglog's <c>unstable-fn</c> makes one: <paramref name="target"/> applied
        /// to <paramref name="arguments"/>, its first parameters, then to the inputs.
        /// </summary>
        /// <typeparam name="T1">The type of input 1.</typeparam>
        /// <typeparam name="T2">The type of input 2.</typeparam>
        /// <typeparam name="TResult">The type of the output, the target's result.</typeparam>
        /// <param name="target">A constructor or a function.</param>
        /// <param name="arguments">The partially applied arguments, one per parameter before the inputs.</param>
        /// <returns>The value.</returns>
        /// <exception cref="ArgumentException">The target is a relation, its parameters are not the arguments' then the
        /// inputs, its result is not the output, or an argument is not of its parameter's type.</exception>
        public static UnstableFn<T1, T2, TResult> Of<T1, T2, TResult>(TableHandle target, params ReadOnlySpan<object> arguments)
            where T1 : notnull
            where T2 : notnull
            where TResult : notnull
            => new(FnValue.Create(target, arguments, [typeof(T1), typeof(T2)], typeof(TResult)));

        /// <summary>
        /// Makes a function value on the host, as egglog's <c>unstable-fn</c> makes one: <paramref name="target"/> applied
        /// to <paramref name="arguments"/>, its first parameters, then to the inputs.
        /// </summary>
        /// <typeparam name="T1">The type of input 1.</typeparam>
        /// <typeparam name="T2">The type of input 2.</typeparam>
        /// <typeparam name="TResult">The type of the output, the target's result.</typeparam>
        /// <param name="target">A primitive.</param>
        /// <param name="arguments">The partially applied arguments, one per parameter before the inputs.</param>
        /// <returns>The value.</returns>
        /// <exception cref="ArgumentException">The target's parameters are not the arguments' then the inputs, its result
        /// is not the output, or an argument is not of its parameter's type.</exception>
        public static UnstableFn<T1, T2, TResult> Of<T1, T2, TResult>(Primitive target, params ReadOnlySpan<object> arguments)
            where T1 : notnull
            where T2 : notnull
            where TResult : notnull
            => new(FnValue.Create(target, arguments, [typeof(T1), typeof(T2)], typeof(TResult)));

        /// <summary>
        /// Makes a function value on the host, as egglog's <c>unstable-fn</c> makes one: <paramref name="target"/> applied
        /// to <paramref name="arguments"/>, its first parameters, then to the inputs.
        /// </summary>
        /// <typeparam name="T1">The type of input 1.</typeparam>
        /// <typeparam name="T2">The type of input 2.</typeparam>
        /// <typeparam name="T3">The type of input 3.</typeparam>
        /// <typeparam name="TResult">The type of the output, the target's result.</typeparam>
        /// <param name="target">A constructor or a function.</param>
        /// <param name="arguments">The partially applied arguments, one per parameter before the inputs.</param>
        /// <returns>The value.</returns>
        /// <exception cref="ArgumentException">The target is a relation, its parameters are not the arguments' then the
        /// inputs, its result is not the output, or an argument is not of its parameter's type.</exception>
        public static UnstableFn<T1, T2, T3, TResult> Of<T1, T2, T3, TResult>(TableHandle target, params ReadOnlySpan<object> arguments)
            where T1 : notnull
            where T2 : notnull
            where T3 : notnull
            where TResult : notnull
            => new(FnValue.Create(target, arguments, [typeof(T1), typeof(T2), typeof(T3)], typeof(TResult)));

        /// <summary>
        /// Makes a function value on the host, as egglog's <c>unstable-fn</c> makes one: <paramref name="target"/> applied
        /// to <paramref name="arguments"/>, its first parameters, then to the inputs.
        /// </summary>
        /// <typeparam name="T1">The type of input 1.</typeparam>
        /// <typeparam name="T2">The type of input 2.</typeparam>
        /// <typeparam name="T3">The type of input 3.</typeparam>
        /// <typeparam name="TResult">The type of the output, the target's result.</typeparam>
        /// <param name="target">A primitive.</param>
        /// <param name="arguments">The partially applied arguments, one per parameter before the inputs.</param>
        /// <returns>The value.</returns>
        /// <exception cref="ArgumentException">The target's parameters are not the arguments' then the inputs, its result
        /// is not the output, or an argument is not of its parameter's type.</exception>
        public static UnstableFn<T1, T2, T3, TResult> Of<T1, T2, T3, TResult>(Primitive target, params ReadOnlySpan<object> arguments)
            where T1 : notnull
            where T2 : notnull
            where T3 : notnull
            where TResult : notnull
            => new(FnValue.Create(target, arguments, [typeof(T1), typeof(T2), typeof(T3)], typeof(TResult)));

        /// <summary>
        /// Makes a function value on the host, as egglog's <c>unstable-fn</c> makes one: <paramref name="target"/> applied
        /// to <paramref name="arguments"/>, its first parameters, then to the inputs.
        /// </summary>
        /// <typeparam name="T1">The type of input 1.</typeparam>
        /// <typeparam name="T2">The type of input 2.</typeparam>
        /// <typeparam name="T3">The type of input 3.</typeparam>
        /// <typeparam name="T4">The type of input 4.</typeparam>
        /// <typeparam name="TResult">The type of the output, the target's result.</typeparam>
        /// <param name="target">A constructor or a function.</param>
        /// <param name="arguments">The partially applied arguments, one per parameter before the inputs.</param>
        /// <returns>The value.</returns>
        /// <exception cref="ArgumentException">The target is a relation, its parameters are not the arguments' then the
        /// inputs, its result is not the output, or an argument is not of its parameter's type.</exception>
        public static UnstableFn<T1, T2, T3, T4, TResult> Of<T1, T2, T3, T4, TResult>(TableHandle target, params ReadOnlySpan<object> arguments)
            where T1 : notnull
            where T2 : notnull
            where T3 : notnull
            where T4 : notnull
            where TResult : notnull
            => new(FnValue.Create(target, arguments, [typeof(T1), typeof(T2), typeof(T3), typeof(T4)], typeof(TResult)));

        /// <summary>
        /// Makes a function value on the host, as egglog's <c>unstable-fn</c> makes one: <paramref name="target"/> applied
        /// to <paramref name="arguments"/>, its first parameters, then to the inputs.
        /// </summary>
        /// <typeparam name="T1">The type of input 1.</typeparam>
        /// <typeparam name="T2">The type of input 2.</typeparam>
        /// <typeparam name="T3">The type of input 3.</typeparam>
        /// <typeparam name="T4">The type of input 4.</typeparam>
        /// <typeparam name="TResult">The type of the output, the target's result.</typeparam>
        /// <param name="target">A primitive.</param>
        /// <param name="arguments">The partially applied arguments, one per parameter before the inputs.</param>
        /// <returns>The value.</returns>
        /// <exception cref="ArgumentException">The target's parameters are not the arguments' then the inputs, its result
        /// is not the output, or an argument is not of its parameter's type.</exception>
        public static UnstableFn<T1, T2, T3, T4, TResult> Of<T1, T2, T3, T4, TResult>(Primitive target, params ReadOnlySpan<object> arguments)
            where T1 : notnull
            where T2 : notnull
            where T3 : notnull
            where T4 : notnull
            where TResult : notnull
            => new(FnValue.Create(target, arguments, [typeof(T1), typeof(T2), typeof(T3), typeof(T4)], typeof(TResult)));

    }

}

using System;

namespace Eggnog
{

    /// <summary>
    /// A function of any number of arguments of one type: the function of a <see cref="VariadicPrimitive{T, TResult}"/>,
    /// which <see cref="Primitive.CreateVariadic{T, TResult}(string, VariadicFunc{T, TResult})"/> makes.
    /// <see cref="Func{T, TResult}"/> cannot take a span on every target framework.
    /// </summary>
    /// <typeparam name="T">The arguments' type.</typeparam>
    /// <typeparam name="TResult">The result type.</typeparam>
    /// <param name="arguments">The arguments, valid only during the call; a function that keeps them copies them.</param>
    /// <returns>The result.</returns>
    public delegate TResult VariadicFunc<T, TResult>(ReadOnlySpan<T> arguments);

}

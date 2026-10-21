using System;

namespace Eggnog
{

    /// <summary>
    /// A function of any number of arguments: the function of a primitive of any arity, which
    /// <see cref="Primitive.Create{TResult}(string, System.Collections.Generic.IReadOnlyList{Type}, FuncN{TResult})"/>
    /// makes. <see cref="Func{T, TResult}"/> cannot take a span on every target framework.
    /// </summary>
    /// <typeparam name="TResult">The result type.</typeparam>
    /// <param name="arguments">The arguments, one per parameter, each of its parameter's type, valid only during the
    /// call; a function that keeps them copies them.</param>
    /// <returns>The result.</returns>
    public delegate TResult FuncN<TResult>(ReadOnlySpan<object> arguments);

}

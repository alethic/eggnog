using System;
using System.Collections.Generic;

namespace Eggnog
{

    /// <summary>
    /// A partial function of any number of arguments, the shape of egglog's <c>-?&gt;</c> primitives: it returns
    /// <c>false</c> when it has no result for its arguments.
    /// <see cref="Primitive.CreatePartial{TResult}(string, IReadOnlyList{Type}, PartialFuncN{TResult})"/> makes a
    /// primitive of it. Its <c>out</c> parameter carries no nullability attribute, as <see cref="PartialFunc{T1, TResult}"/>.
    /// </summary>
    /// <typeparam name="TResult">The result type.</typeparam>
    /// <param name="arguments">The arguments, one per parameter, each of its parameter's type, valid only during the
    /// call; a function that keeps them copies them.</param>
    /// <param name="result">The result, when the function returns <c>true</c>; otherwise ignored, so any value will
    /// do (<c>default!</c>).</param>
    /// <returns>Whether there is a result.</returns>
    public delegate bool PartialFuncN<TResult>(ReadOnlySpan<object> arguments, out TResult result);

}

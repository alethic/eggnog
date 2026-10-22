using System;

namespace Eggnog
{

    /// <summary>
    /// The function of a primitive that reads or writes tables, egglog's read, write or full primitive's <c>apply</c>:
    /// it gets the tables the call may use and the arguments, and returns <c>false</c> where it has no result, as egglog's
    /// returns <c>None</c>. <see cref="Primitive.CreateRead{TResult}"/>, <see cref="Primitive.CreateWrite{TResult}"/> and
    /// <see cref="Primitive.CreateFull{TResult}"/> make a primitive of it. Its <c>out</c> parameter carries no nullability
    /// attribute, as <see cref="PartialFunc{T1, TResult}"/>.
    /// </summary>
    /// <typeparam name="TTables">What the function may do to the tables: <see cref="ITableReader"/>,
    /// <see cref="ITableWriter"/> or <see cref="ITables"/>.</typeparam>
    /// <typeparam name="TResult">The result type.</typeparam>
    /// <param name="tables">The tables, valid only during the call.</param>
    /// <param name="arguments">The arguments, one per parameter, each of its parameter's type, valid only during the
    /// call; a function that keeps them copies them.</param>
    /// <param name="result">The result, when the function returns <c>true</c>; otherwise ignored, so any value will
    /// do (<c>default!</c>).</param>
    /// <returns>Whether there is a result.</returns>
    public delegate bool TableFuncN<in TTables, TResult>(TTables tables, ReadOnlySpan<object> arguments, out TResult result);

}

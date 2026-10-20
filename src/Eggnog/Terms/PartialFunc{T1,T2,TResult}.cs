namespace Eggnog
{

    /// <summary>
    /// A partial function of two arguments, the shape of egglog's <c>-?&gt;</c> primitives: it returns <c>false</c> when it
    /// has no result for its arguments. <see cref="Primitive.CreatePartial{T1, T2, TResult}(string, PartialFunc{T1, T2, TResult})"/>
    /// makes a primitive of it.
    /// </summary>
    /// <typeparam name="T1">The first argument type.</typeparam>
    /// <typeparam name="T2">The second argument type.</typeparam>
    /// <typeparam name="TResult">The result type.</typeparam>
    /// <param name="arg1">The first argument.</param>
    /// <param name="arg2">The second argument.</param>
    /// <param name="result">The result, when the function returns <c>true</c>; otherwise ignored, so any value will
    /// do (<c>default!</c>).</param>
    /// <returns>Whether there is a result.</returns>
    public delegate bool PartialFunc<in T1, in T2, TResult>(T1 arg1, T2 arg2, out TResult result);

}

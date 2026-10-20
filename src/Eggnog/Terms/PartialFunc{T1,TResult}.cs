namespace Eggnog
{

    /// <summary>
    /// A partial function of one argument, the shape of egglog's <c>-?&gt;</c> primitives: it returns <c>false</c> when it
    /// has no result for its arguments. <see cref="Primitive.CreatePartial{T1, TResult}(string, PartialFunc{T1, TResult})"/>
    /// makes a primitive of it. Its <c>out</c> parameter carries no nullability attribute, so that a lambda with explicit
    /// parameter types converts without a warning; a .NET <c>Try</c> method annotated <c>[MaybeNullWhen(false)]</c>, such
    /// as <c>TryGetValue</c>, converts with one, and is better wrapped in a lambda.
    /// </summary>
    /// <typeparam name="T1">The argument type.</typeparam>
    /// <typeparam name="TResult">The result type.</typeparam>
    /// <param name="arg1">The argument.</param>
    /// <param name="result">The result, when the function returns <c>true</c>; otherwise ignored, so any value will
    /// do (<c>default!</c>).</param>
    /// <returns>Whether there is a result.</returns>
    public delegate bool PartialFunc<in T1, TResult>(T1 arg1, out TResult result);

}

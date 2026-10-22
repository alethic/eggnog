namespace Eggnog
{

    /// <summary>
    /// A function value of any arity (<see cref="UnstableFn{T1, TResult}"/> and the others): its content, <c>null</c> for a
    /// default value, which applies nothing.
    /// </summary>
    internal interface IFnValue
    {

        /// <summary>
        /// Gets the content, <c>null</c> for a default value.
        /// </summary>
        FnValue? Content { get; }

    }

}

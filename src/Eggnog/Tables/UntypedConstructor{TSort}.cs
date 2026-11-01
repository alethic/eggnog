namespace Eggnog
{

    /// <summary>
    /// A constructor of any arity, made by the untyped <c>EGraph.DefineConstructor</c> overload through its sort's column
    /// type (<see cref="ColumnType.CreateUntypedConstructor"/>), so that it carries its sort as the typed constructors do,
    /// and applied with <see cref="ConstructorHandle.Apply"/>. Internal; the public typed handles take up to four key
    /// columns.
    /// </summary>
    /// <typeparam name="TSort">The sort of the terms.</typeparam>
    internal sealed class UntypedConstructor<TSort> : ConstructorHandle<TSort> where TSort : struct, ISort<TSort>
    {

        /// <summary>
        /// Initializes a new instance; the graph sets the definition.
        /// </summary>
        internal UntypedConstructor()
        {

        }

    }

}

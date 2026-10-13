namespace Eggnog
{

    /// <summary>
    /// A constructor of any arity, made by the untyped <c>EGraph.DefineConstructor</c> overload and applied with
    /// <see cref="ConstructorHandle.Apply"/>. Internal; the public typed handles take up to four key columns.
    /// </summary>
    internal sealed class UntypedConstructor : ConstructorHandle
    {

        /// <summary>
        /// Initializes a new instance; the graph sets the definition.
        /// </summary>
        internal UntypedConstructor()
        {

        }

    }

}

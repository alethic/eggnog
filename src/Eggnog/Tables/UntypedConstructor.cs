namespace Eggnog
{

    /// <summary>
    /// A constructor of any arity, made by the untyped <c>EGraph.DefineConstructor</c> overload (D13) and applied with
    /// <see cref="ConstructorHandle.Apply"/>. Internal until a consumer needs more than four columns.
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

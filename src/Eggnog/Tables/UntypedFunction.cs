namespace Eggnog
{

    /// <summary>
    /// A function of any arity, made by the untyped <c>EGraph.DefineFunction</c> overload (D13) and applied with
    /// <see cref="FunctionHandle.Apply"/>. Internal until a consumer needs more than four columns.
    /// </summary>
    internal sealed class UntypedFunction : FunctionHandle
    {

        /// <summary>
        /// Initializes a new instance; the graph sets the definition.
        /// </summary>
        internal UntypedFunction()
        {

        }

    }

}

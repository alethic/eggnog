namespace Eggnog
{

    /// <summary>
    /// A function of any arity, made by the untyped <c>EGraph.DefineFunction</c> overload and applied with
    /// <see cref="FunctionHandle.Apply"/>. Internal; the public typed handles take up to four key columns.
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

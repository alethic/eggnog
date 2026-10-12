namespace Eggnog
{

    /// <summary>
    /// A relation of any arity, made by the untyped <c>EGraph.DefineRelation</c> overload (D13) and applied with
    /// <see cref="RelationHandle.Apply"/>. Internal until a consumer needs more than four columns.
    /// </summary>
    internal sealed class UntypedRelation : RelationHandle
    {

        /// <summary>
        /// Initializes a new instance; the graph sets the definition.
        /// </summary>
        internal UntypedRelation()
        {

        }

    }

}

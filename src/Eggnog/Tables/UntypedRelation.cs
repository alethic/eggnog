namespace Eggnog
{

    /// <summary>
    /// A relation of any arity, made by the untyped <c>EGraph.DefineRelation</c> overload and applied with
    /// <see cref="RelationHandle.Apply"/>. Internal; the public typed handles take up to four key columns.
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

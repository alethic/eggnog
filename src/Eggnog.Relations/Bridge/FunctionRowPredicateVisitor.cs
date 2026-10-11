namespace Eggnog.Relations
{

    /// <summary>
    /// Adapts a <see cref="FunctionRowPredicate"/> to <see cref="IFunctionRowVisitor"/>: one delegate call per row.
    /// </summary>
    internal readonly struct FunctionRowPredicateVisitor : IFunctionRowVisitor
    {

        readonly FunctionRowPredicate predicate;

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="predicate">The callback.</param>
        public FunctionRowPredicateVisitor(FunctionRowPredicate predicate)
        {
            this.predicate = predicate;
        }

        /// <inheritdoc />
        public bool Visit(FunctionRow row) => predicate(row);

    }

}

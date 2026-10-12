namespace Eggnog
{

    /// <summary>
    /// The filter that a boolean term is true: in a rule body only.
    /// </summary>
    internal sealed class FilterFact : Fact
    {

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="condition">The condition, of CLR type <c>bool</c>.</param>
        public FilterFact(Term condition)
        {
            Condition = condition;
        }

        /// <summary>
        /// Gets the condition.
        /// </summary>
        public Term Condition { get; }

        /// <inheritdoc />
        internal override FactKind Kind => FactKind.Filter;

    }

}

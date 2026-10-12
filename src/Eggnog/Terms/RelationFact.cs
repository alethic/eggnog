namespace Eggnog
{

    /// <summary>
    /// The fact that a relation holds a tuple.
    /// </summary>
    internal sealed class RelationFact : Fact
    {

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="table">The relation.</param>
        /// <param name="arguments">One term per key.</param>
        public RelationFact(RelationHandle table, Term[] arguments)
        {
            Table = table;
            Arguments = arguments;
        }

        /// <summary>
        /// Gets the relation.
        /// </summary>
        public RelationHandle Table { get; }

        /// <summary>
        /// Gets the tuple's terms.
        /// </summary>
        public Term[] Arguments { get; }

        /// <inheritdoc />
        internal override FactKind Kind => FactKind.Relation;

    }

}

namespace Eggnog
{

    /// <summary>
    /// <see cref="Fact.In"/>: facts whose every table application, nested ones included, reads the nearest
    /// ancestor-or-self of the running scope tagged <see cref="Scope"/>. A rule or query refuses one that applies a
    /// table with a sort column. Immutable.
    /// </summary>
    internal sealed class ScopedFact : Fact
    {

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="scope">The tag.</param>
        /// <param name="facts">The facts, copied by the caller.</param>
        public ScopedFact(ScopeTag scope, Fact[] facts)
        {
            Scope = scope;
            Facts = facts;
        }

        /// <summary>
        /// Gets the tag of the scope the facts read.
        /// </summary>
        public ScopeTag Scope { get; }

        /// <summary>
        /// Gets the facts.
        /// </summary>
        public Fact[] Facts { get; }

        /// <inheritdoc />
        internal override FactKind Kind => FactKind.Scoped;

        /// <summary>
        /// Gets the first table in <paramref name="fact"/> that belongs to another graph than <paramref name="graph"/>,
        /// so that <see cref="Fact.In"/> can refuse a tag of another graph than the facts' tables, since a tag belongs
        /// to one graph; <c>null</c> when there is none.
        /// </summary>
        /// <param name="fact">The fact.</param>
        /// <param name="graph">The tag's graph.</param>
        /// <returns>The foreign table, or <c>null</c>.</returns>
        public static TableHandle? FindForeignTable(Fact fact, EGraph graph)
        {
            switch (fact)
            {
                case RelationFact relation:
                    if (ReferenceEquals(relation.Table.Graph, graph) == false)
                        return relation.Table;

                    return FindForeignTable(relation.Arguments, graph);
                case EqualityFact equality:
                    return FindForeignTable(equality.Left, graph) ?? FindForeignTable(equality.Right, graph);
                case NotEqualFact notEqual:
                    return FindForeignTable(notEqual.Left, graph) ?? FindForeignTable(notEqual.Right, graph);
                case ExistsFact exists:
                    return FindForeignTable(exists.Application, graph);
                case FilterFact filter:
                    return FindForeignTable(filter.Condition, graph);
                case ScopedFact scoped:
                    foreach (var inner in scoped.Facts)
                        if (FindForeignTable(inner, graph) is { } table)
                            return table;

                    return null;
                default:
                    return null;
            }
        }

        /// <summary>
        /// Gets the first table in a term that belongs to another graph.
        /// </summary>
        static TableHandle? FindForeignTable(Term term, EGraph graph)
        {
            var shape = term.Shape;
            if (shape.Kind == TermShapeKind.Application && ReferenceEquals(shape.Table!.Graph, graph) == false)
                return shape.Table;

            return shape.Kind is TermShapeKind.Application or TermShapeKind.Computation ? FindForeignTable(shape.Arguments, graph) : null;
        }

        /// <summary>
        /// Gets the first table in some terms that belongs to another graph.
        /// </summary>
        static TableHandle? FindForeignTable(Term[] terms, EGraph graph)
        {
            foreach (var term in terms)
                if (FindForeignTable(term, graph) is { } table)
                    return table;

            return null;
        }

    }

}

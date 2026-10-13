using System;

namespace Eggnog
{

    /// <summary>
    /// A relation over 0 key columns: a set of tuples, defined with
    /// <see cref="EGraph.DefineRelation(string, TableOptions)"/>.
    /// </summary>
    public sealed class Relation : RelationHandle
    {

        /// <summary>
        /// Initializes a new instance; the graph sets the definition.
        /// </summary>
        internal Relation()
        {

        }

        /// <summary>
        /// The fact that the relation holds the tuple: a join in a body, an insertion in a head, a row in a host write.
        /// </summary>
        /// <returns>The fact.</returns>
        public Fact Of() => new RelationFact(this, Array.Empty<Term>());

        /// <summary>
        /// A read-only scoped view of the table, which must have no sort column: an atom over it reads the nearest
        /// ancestor-or-self of the running scope tagged <paramref name="scope"/>; a host read reads that scope
        /// directly. The view keeps this type, so it composes with helpers that take the table; a write through it is a
        /// definition error in a rule and an <see cref="ArgumentException"/> in a host write.
        /// </summary>
        /// <param name="scope">The tag of the scope to read.</param>
        /// <returns>The view.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="scope"/> is <c>null</c>.</exception>
        /// <exception cref="ArgumentException">The tag belongs to another graph, or the table has a sort
        /// column.</exception>
        public Relation In(ScopeTag scope) => ViewIn(new Relation(), scope);

    }

}

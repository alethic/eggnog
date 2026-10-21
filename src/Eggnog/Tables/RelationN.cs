using System;
using System.Collections.Generic;

namespace Eggnog
{

    /// <summary>
    /// A relation over any number of key columns, as egglog's relations take: a set of tuples, defined with
    /// <see cref="EGraph.DefineRelationN(string, IReadOnlyList{Type}, TableOptions)"/>
    /// where the typed relations stop at four columns.
    /// </summary>
    public sealed class RelationN : RelationHandle
    {

        /// <summary>
        /// Initializes a new instance; the graph sets the definition.
        /// </summary>
        internal RelationN()
        {

        }

        /// <summary>
        /// The fact that the relation holds the tuple: a join in a body, an insertion in a head, a row in a host write.
        /// </summary>
        /// <param name="keys">One argument per key column, of the column's type: a term, or a value, which becomes a
        /// constant (an instance of the column's type: <c>1L</c> for a <c>long</c>, not <c>1</c>).</param>
        /// <returns>The fact.</returns>
        /// <exception cref="ArgumentException">The number of arguments is not the relation's, or an argument's type is
        /// not its column's.</exception>
        public Fact Of(params ReadOnlySpan<object> keys) => new RelationFact(this, ToArguments(keys));

        /// <summary>
        /// A read-only scoped view of the table, which must have no sort column, as for the typed relations.
        /// </summary>
        /// <param name="scope">The tag of the scope to read.</param>
        /// <returns>The view.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="scope"/> is <c>null</c>.</exception>
        /// <exception cref="ArgumentException">The tag belongs to another graph, or the table has a sort
        /// column.</exception>
        public RelationN In(ScopeTag scope) => ViewIn(new RelationN(), scope);

    }

}

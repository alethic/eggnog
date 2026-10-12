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

    }

}

using System;

namespace Eggnog
{

    /// <summary>
    /// A relation over 1 key column: a set of tuples, defined with
    /// <see cref="EGraph.DefineRelation{T1}(string, TableOptions)"/>.
    /// </summary>
    /// <typeparam name="T1">The type of key 1.</typeparam>
    public sealed class Relation<T1> : RelationHandle
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
        /// <param name="a">Key 1.</param>
        /// <returns>The fact.</returns>
        public Fact Of(Term<T1> a) => new RelationFact(this, new Term[] { Check(a, nameof(a)) });

        /// <summary>
        /// Checks that an argument is not <c>null</c>.
        /// </summary>
        static Term Check(Term argument, string parameter) => argument ?? throw new ArgumentNullException(parameter);

    }

}

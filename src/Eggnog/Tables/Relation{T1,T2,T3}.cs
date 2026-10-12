using System;

namespace Eggnog
{

    /// <summary>
    /// A relation over 3 key columns: a set of tuples, defined with
    /// <see cref="EGraph.DefineRelation{T1, T2, T3}(string, TableOptions)"/>.
    /// </summary>
    /// <typeparam name="T1">The type of key 1.</typeparam>
    /// <typeparam name="T2">The type of key 2.</typeparam>
    /// <typeparam name="T3">The type of key 3.</typeparam>
    public sealed class Relation<T1, T2, T3> : RelationHandle
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
        /// <param name="b">Key 2.</param>
        /// <param name="c">Key 3.</param>
        /// <returns>The fact.</returns>
        public Fact Of(Term<T1> a, Term<T2> b, Term<T3> c) => new RelationFact(this, new Term[] { Check(a, nameof(a)), Check(b, nameof(b)), Check(c, nameof(c)) });

        /// <summary>
        /// Checks that an argument is not <c>null</c>.
        /// </summary>
        static Term Check(Term argument, string parameter) => argument ?? throw new ArgumentNullException(parameter);

    }

}

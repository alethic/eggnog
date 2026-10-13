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
        public Relation<T1, T2, T3> In(ScopeTag scope) => ViewIn(new Relation<T1, T2, T3>(), scope);

    }

}

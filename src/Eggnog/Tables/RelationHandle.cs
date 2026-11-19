namespace Eggnog
{

    /// <summary>
    /// The base of the typed relations. A relation is a set of tuples: an engine function whose output is the unit
    /// value. Its untyped members, which take any arity, are internal.
    /// </summary>
    public abstract class RelationHandle : TableHandle
    {

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        private protected RelationHandle()
        {

        }

        /// <summary>
        /// The untyped application, of any arity: the fact that the relation holds the tuple
        /// <paramref name="arguments"/>.
        /// </summary>
        /// <param name="arguments">One term per key, of the key's CLR type.</param>
        /// <returns>The fact.</returns>
        internal Fact Apply(params Term[] arguments)
        {
            CheckArguments(arguments);
            return new RelationFact(this, (Term[])arguments.Clone());
        }

    }

}

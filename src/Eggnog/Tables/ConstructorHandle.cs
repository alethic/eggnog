namespace Eggnog
{

    /// <summary>
    /// The base of the typed constructors: term heads, whose rows map keys to the class of the term.
    /// </summary>
    public abstract class ConstructorHandle : TableHandle
    {

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        private protected ConstructorHandle()
        {

        }

        /// <summary>
        /// Gets the declared cost of a node with this head, without its children (extraction's default).
        /// </summary>
        public long Cost { get; internal set; }

        /// <summary>
        /// Gets whether extraction may use the constructor's rows.
        /// </summary>
        public bool Extractable { get; internal set; }

        /// <summary>
        /// The untyped application, of any arity: the term with this head and <paramref name="arguments"/>.
        /// </summary>
        /// <param name="arguments">One term per key, of the key's CLR type.</param>
        /// <returns>The term.</returns>
        internal Term Apply(params Term[] arguments)
        {
            CheckArguments(arguments);
            return new UntypedApplicationTerm(this, (Term[])arguments.Clone());
        }

    }

}

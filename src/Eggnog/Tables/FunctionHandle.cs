namespace Eggnog
{

    /// <summary>
    /// The base of the typed functions: maps from keys to a value, merged by a lattice or, without one, required to agree.
    /// </summary>
    public abstract class FunctionHandle : TableHandle
    {

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        private protected FunctionHandle()
        {

        }

        /// <summary>
        /// Gets the lattice that merges the function's values; <c>null</c> when two different values are an error.
        /// </summary>
        public ValueLattice? Lattice { get; internal set; }

        /// <summary>
        /// The untyped application: the function's value at <paramref name="arguments"/> (D13).
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

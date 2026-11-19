namespace Eggnog
{

    /// <summary>
    /// The base of every function, whatever its output type: maps from keys to a value or a class, merged by a lattice
    /// or a <see cref="FunctionMerge"/>, or, without either, required to agree. The members that cannot know the output
    /// type statically take or return this base; <see cref="FunctionHandle{TOut}"/> is the typed base, which carries
    /// it.
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
        /// Gets the lattice that merges the function's values; <c>null</c> when two different values are an error, or
        /// the function has a merge that is not a lattice.
        /// </summary>
        public ValueLattice? Lattice { get; internal set; }

        /// <summary>
        /// Gets the function's merge that is not a lattice (<see cref="FunctionMerge{T}"/>), which only the root writes;
        /// <c>null</c> for a function with a lattice or none.
        /// </summary>
        internal FunctionMergeDefinition? Merge { get; set; }

        /// <summary>
        /// The untyped application, of any arity: the function's value at <paramref name="arguments"/>.
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

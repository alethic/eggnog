namespace Eggnog
{

    /// <summary>
    /// The base of the typed functions of one output type (<see cref="Function{TOut}"/> and its arities,
    /// <see cref="FunctionN{TOut}"/>): what a call that reads or writes a function's value takes, so that the output's type
    /// is inferred from the function, as in
    /// <see cref="ITableReader.TryLookup{TOut}(FunctionHandle{TOut}, System.ReadOnlySpan{object}, out TOut)"/>.
    /// </summary>
    /// <typeparam name="TOut">The type of the value.</typeparam>
    public abstract class FunctionHandle<TOut> : FunctionHandle
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
        public new ValueLattice<TOut>? Lattice => (ValueLattice<TOut>?)base.Lattice;

    }

}

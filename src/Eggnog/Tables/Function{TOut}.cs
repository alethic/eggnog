using System;

namespace Eggnog
{

    /// <summary>
    /// A function over 0 key columns with a value output, defined with
    /// <see cref="EGraph.DefineFunction{TOut}(string, ValueLattice{TOut}, TableOptions)"/>.
    /// </summary>
    /// <typeparam name="TOut">The type of the value.</typeparam>
    public sealed class Function<TOut> : FunctionHandle
    {

        /// <summary>
        /// Initializes a new instance; the graph sets the definition.
        /// </summary>
        internal Function()
        {

        }

        /// <summary>
        /// Gets the lattice that merges the function's values; <c>null</c> when two different values are an error.
        /// </summary>
        public new ValueLattice<TOut>? Lattice => (ValueLattice<TOut>?)base.Lattice;

        /// <summary>
        /// The function's value at the keys: matched in a body (<c>d.Is(dom.Of(p))</c>), written in a head or a host write
        /// (<c>dom.Of(p).Is(d)</c>), read by <see cref="Scope.Get{T}(Term{T})"/>.
        /// </summary>
        /// <returns>The term.</returns>
        public Term<TOut> Of() => new ApplicationTerm<TOut>(this, Array.Empty<Term>());

    }

}

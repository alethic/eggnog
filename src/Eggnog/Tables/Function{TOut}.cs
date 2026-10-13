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

        /// <summary>
        /// A read-only scoped view of the table (D1, §6.8): an atom over it reads the nearest ancestor-or-self of the
        /// running scope tagged <paramref name="scope"/>; a host read reads that scope directly. The view keeps this type,
        /// so it composes with helpers that take the table; a write through it is a definition error in a rule and an
        /// <see cref="ArgumentException"/> in a host write.
        /// </summary>
        /// <param name="scope">The tag of the scope to read.</param>
        /// <returns>The view.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="scope"/> is <c>null</c>.</exception>
        /// <exception cref="ArgumentException">The tag belongs to another graph, or the table has a sort column.</exception>
        public Function<TOut> In(ScopeTag scope) => ViewIn(new Function<TOut>(), scope);

    }

}

using System;
using System.Collections.Generic;

namespace Eggnog
{

    /// <summary>
    /// A function over any number of key columns, as egglog's functions take, with a value or class output, defined with
    /// <see cref="EGraph.DefineFunctionN{TOut}(string, IReadOnlyList{Type}, ValueLattice{TOut}, TableOptions)"/>
    /// where the typed functions stop at four keys.
    /// </summary>
    /// <typeparam name="TOut">The type of the value.</typeparam>
    public sealed class FunctionN<TOut> : FunctionHandle
    {

        /// <summary>
        /// Initializes a new instance; the graph sets the definition.
        /// </summary>
        internal FunctionN()
        {

        }

        /// <summary>
        /// Gets the lattice that merges the function's values; <c>null</c> when it has none.
        /// </summary>
        public new ValueLattice<TOut>? Lattice => (ValueLattice<TOut>?)base.Lattice;

        /// <summary>
        /// The function's value at the keys: matched in a body, written in a head or a host write, read by
        /// <see cref="Scope.Get{T}(Term{T})"/>.
        /// </summary>
        /// <param name="keys">One argument per key column, of the column's type: a term, or a value, which becomes a
        /// constant (an instance of the column's type: <c>1L</c> for a <c>long</c>, not <c>1</c>).</param>
        /// <returns>The term.</returns>
        /// <exception cref="ArgumentException">The number of arguments is not the function's, or an argument's type is
        /// not its column's.</exception>
        public Term<TOut> Of(params ReadOnlySpan<object> keys) => new ApplicationTerm<TOut>(this, ToArguments(keys));

        /// <summary>
        /// A read-only scoped view of the table, which must have no sort column, as for the typed functions.
        /// </summary>
        /// <param name="scope">The tag of the scope to read.</param>
        /// <returns>The view.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="scope"/> is <c>null</c>.</exception>
        /// <exception cref="ArgumentException">The tag belongs to another graph, or the table has a sort
        /// column.</exception>
        public FunctionN<TOut> In(ScopeTag scope) => ViewIn(new FunctionN<TOut>(), scope);

    }

}

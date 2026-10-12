using System;

namespace Eggnog
{

    /// <summary>
    /// A function over 4 key columns with a value output, defined with
    /// <see cref="EGraph.DefineFunction{T1, T2, T3, T4, TOut}(string, ValueLattice{TOut}, TableOptions)"/>.
    /// </summary>
    /// <typeparam name="T1">The type of key 1.</typeparam>
    /// <typeparam name="T2">The type of key 2.</typeparam>
    /// <typeparam name="T3">The type of key 3.</typeparam>
    /// <typeparam name="T4">The type of key 4.</typeparam>
    /// <typeparam name="TOut">The type of the value.</typeparam>
    public sealed class Function<T1, T2, T3, T4, TOut> : FunctionHandle
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
        /// <param name="a">Key 1.</param>
        /// <param name="b">Key 2.</param>
        /// <param name="c">Key 3.</param>
        /// <param name="d">Key 4.</param>
        /// <returns>The term.</returns>
        public Term<TOut> Of(Term<T1> a, Term<T2> b, Term<T3> c, Term<T4> d) => new ApplicationTerm<TOut>(this, new Term[] { Check(a, nameof(a)), Check(b, nameof(b)), Check(c, nameof(c)), Check(d, nameof(d)) });

        /// <summary>
        /// Checks that an argument is not <c>null</c>.
        /// </summary>
        static Term Check(Term argument, string parameter) => argument ?? throw new ArgumentNullException(parameter);

    }

}

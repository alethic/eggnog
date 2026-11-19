using System;

namespace Eggnog
{

    /// <summary>
    /// A function over 0 key columns, with a value or class output, defined with
    /// <see cref="EGraph.DefineFunction{TOut}(string, ValueLattice{TOut}, FunctionOptions)"/>.
    /// </summary>
    /// <typeparam name="TOut">The type of the value.</typeparam>
    public sealed class Function<TOut> : FunctionHandle<TOut>
    {

        /// <summary>
        /// Initializes a new instance; the graph sets the definition.
        /// </summary>
        internal Function()
        {

        }

        /// <summary>
        /// The function's value at the keys: matched in a body (<c>d.Is(dom.Of(p))</c>), written in a head or a host
        /// write (<c>dom.Of(p).Is(d)</c>), read by <see cref="Scope.Get{T}(Term{T})"/>.
        /// </summary>
        /// <returns>The term.</returns>
        public Term<TOut> Of() => new ApplicationTerm<TOut>(this, Array.Empty<Term>());

    }

}

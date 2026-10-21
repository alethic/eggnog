using System;
using System.Collections.Generic;

namespace Eggnog
{

    /// <summary>
    /// A constructor over any number of key columns, as egglog's constructors take: a term head whose rows map keys to the
    /// class of the term, defined with
    /// <see cref="EGraph.DefineConstructorN{TSort}(string, IReadOnlyList{Type}, long, TableOptions)"/>
    /// where the typed constructors stop at four children.
    /// </summary>
    /// <typeparam name="TSort">The sort of the terms.</typeparam>
    public sealed class ConstructorN<TSort> : ConstructorHandle where TSort : struct, ISort<TSort>
    {

        /// <summary>
        /// Initializes a new instance; the graph sets the definition.
        /// </summary>
        internal ConstructorN()
        {

        }

        /// <summary>
        /// The term: a pattern in a body, made in a head, a host write, <see cref="Scope.Make{TSort}"/> or a read.
        /// </summary>
        /// <param name="keys">One argument per key column, of the column's type: a term, or a value, which becomes a
        /// constant (an instance of the column's type: <c>1L</c> for a <c>long</c>, not <c>1</c>).</param>
        /// <returns>The term.</returns>
        /// <exception cref="ArgumentException">The number of arguments is not the constructor's, or an argument's type
        /// is not its column's.</exception>
        public ConstructorTerm<TSort> Of(params object[] keys) => new(this, ToArguments(keys));

    }

}

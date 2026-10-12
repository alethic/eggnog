using System;

namespace Eggnog
{

    /// <summary>
    /// A term constructor over 0 key columns, defined with
    /// <see cref="EGraph.DefineConstructor{TSort}(string, long, TableOptions)"/>.
    /// </summary>
    /// <typeparam name="TSort">The sort of the terms.</typeparam>
    public sealed class Constructor<TSort> : ConstructorHandle where TSort : struct, ISort<TSort>
    {

        /// <summary>
        /// Initializes a new instance; the graph sets the definition.
        /// </summary>
        internal Constructor()
        {

        }

        /// <summary>
        /// The term with this head: matched in a body, made by get-or-add in a head, made by
        /// <see cref="Scope.Make{TSort}(ConstructorTerm{TSort})"/> in a host write.
        /// </summary>
        /// <returns>The term.</returns>
        public ConstructorTerm<TSort> Of() => new(this, Array.Empty<Term>());

    }

}

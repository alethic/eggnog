using System;

namespace Eggnog
{

    /// <summary>
    /// A term constructor over 1 key column, defined with
    /// <see cref="EGraph.DefineConstructor{T1, TSort}(string, ulong, TableOptions)"/>.
    /// </summary>
    /// <typeparam name="T1">The type of key 1.</typeparam>
    /// <typeparam name="TSort">The sort of the terms.</typeparam>
    public sealed class Constructor<T1, TSort> : ConstructorHandle where TSort : struct, ISort<TSort>
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
        /// <param name="a">Key 1.</param>
        /// <returns>The term.</returns>
        public ConstructorTerm<TSort> Of(Term<T1> a) => new(this, new Term[] { Check(a, nameof(a)) });

        /// <summary>
        /// Checks that an argument is not <c>null</c>.
        /// </summary>
        static Term Check(Term argument, string parameter) => argument ?? throw new ArgumentNullException(parameter);

    }

}

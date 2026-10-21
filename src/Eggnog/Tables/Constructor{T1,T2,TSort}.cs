using System;

namespace Eggnog
{

    /// <summary>
    /// A term constructor over 2 key columns, defined with
    /// <see cref="EGraph.DefineConstructor{T1, T2, TSort}(string, ulong, TableOptions)"/>.
    /// </summary>
    /// <typeparam name="T1">The type of key 1.</typeparam>
    /// <typeparam name="T2">The type of key 2.</typeparam>
    /// <typeparam name="TSort">The sort of the terms.</typeparam>
    public sealed class Constructor<T1, T2, TSort> : ConstructorHandle where TSort : struct, ISort<TSort>
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
        /// <param name="b">Key 2.</param>
        /// <returns>The term.</returns>
        public ConstructorTerm<TSort> Of(Term<T1> a, Term<T2> b) => new(this, new Term[] { Check(a, nameof(a)), Check(b, nameof(b)) });

        /// <summary>
        /// Checks that an argument is not <c>null</c>.
        /// </summary>
        static Term Check(Term argument, string parameter) => argument ?? throw new ArgumentNullException(parameter);

    }

}

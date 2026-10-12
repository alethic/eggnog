using System;

namespace Eggnog
{

    /// <summary>
    /// A term constructor over 4 key columns, defined with
    /// <see cref="EGraph.DefineConstructor{T1, T2, T3, T4, TSort}(string, long, TableOptions)"/>.
    /// </summary>
    /// <typeparam name="T1">The type of key 1.</typeparam>
    /// <typeparam name="T2">The type of key 2.</typeparam>
    /// <typeparam name="T3">The type of key 3.</typeparam>
    /// <typeparam name="T4">The type of key 4.</typeparam>
    /// <typeparam name="TSort">The sort of the terms.</typeparam>
    public sealed class Constructor<T1, T2, T3, T4, TSort> : ConstructorHandle where TSort : struct, ISort<TSort>
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
        /// <param name="c">Key 3.</param>
        /// <param name="d">Key 4.</param>
        /// <returns>The term.</returns>
        public ConstructorTerm<TSort> Of(Term<T1> a, Term<T2> b, Term<T3> c, Term<T4> d) => new(this, new Term[] { Check(a, nameof(a)), Check(b, nameof(b)), Check(c, nameof(c)), Check(d, nameof(d)) });

        /// <summary>
        /// Checks that an argument is not <c>null</c>.
        /// </summary>
        static Term Check(Term argument, string parameter) => argument ?? throw new ArgumentNullException(parameter);

    }

}

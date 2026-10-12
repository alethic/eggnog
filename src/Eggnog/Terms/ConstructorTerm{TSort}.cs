using System;

namespace Eggnog
{

    /// <summary>
    /// An application of a constructor: what <see cref="Scope.Make{TSort}(ConstructorTerm{TSort})"/>,
    /// <see cref="ScopeWriter.Make{TSort}(ConstructorTerm{TSort})"/> and
    /// <see cref="RuleBuilder.Make{TSort}(string, ConstructorTerm{TSort})"/> take.
    /// </summary>
    /// <typeparam name="TSort">The sort of the term.</typeparam>
    public sealed class ConstructorTerm<TSort> : Term<TSort> where TSort : struct, ISort<TSort>
    {

        readonly Term[] arguments;

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="head">The constructor.</param>
        /// <param name="arguments">One term per key.</param>
        internal ConstructorTerm(ConstructorHandle head, Term[] arguments)
        {
            Head = head;
            this.arguments = arguments;
        }

        /// <summary>
        /// Gets the constructor.
        /// </summary>
        public ConstructorHandle Head { get; }

        /// <inheritdoc />
        internal override TermShape Shape => new(TermShapeKind.Application, typeof(TSort), null, null, null, null, Head, null, arguments);

    }

}

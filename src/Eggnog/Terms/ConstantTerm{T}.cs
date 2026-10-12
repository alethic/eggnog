using System;

namespace Eggnog
{

    /// <summary>
    /// A typed constant: a value, or for a sort a class (checked where it is used, D39). Its column type is resolved by
    /// the graph it is used in.
    /// </summary>
    /// <typeparam name="T">The CLR type of the value.</typeparam>
    internal sealed class ConstantTerm<T> : Term<T>
    {

        readonly T value;

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="value">The value; not <c>null</c>.</param>
        public ConstantTerm(T value)
        {
            this.value = value;
        }

        /// <summary>
        /// Gets the value.
        /// </summary>
        public T Value => value;

        /// <inheritdoc />
        internal override TermShape Shape => new(TermShapeKind.Constant, typeof(T), null, null, null, value, null, null, Array.Empty<Term>());

    }

}

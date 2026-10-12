using System;

namespace Eggnog
{

    /// <summary>
    /// An expression: a variable, a constant, a table application, or a computation. Immutable; renders as an
    /// s-expression. The untyped constant (<c>Term.Constant</c>) is internal (D13).
    /// </summary>
    public abstract class Term
    {

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        private protected Term()
        {

        }

        /// <summary>
        /// Gets the CLR type of the term's values.
        /// </summary>
        public abstract Type ClrType { get; }

        /// <summary>
        /// Renders the term as an s-expression: <c>(StrEq "kind" p)</c>.
        /// </summary>
        /// <returns>The rendering.</returns>
        public override string ToString() => SExpression.Render(this);

        /// <summary>
        /// Gets the term's structure.
        /// </summary>
        internal abstract TermShape Shape { get; }

        /// <summary>
        /// The untyped constant of a column type (D13).
        /// </summary>
        /// <param name="type">The column type.</param>
        /// <param name="value">The value, of the type's CLR type.</param>
        /// <returns>The term.</returns>
        /// <exception cref="ArgumentException"><paramref name="value"/> is not of the type's CLR type.</exception>
        internal static Term Constant(ColumnType type, object value)
        {
            ArgumentNullException.ThrowIfNull(type);
            ArgumentNullException.ThrowIfNull(value);
            if (type.ClrType.IsInstanceOfType(value) == false)
                throw new ArgumentException($"A {value.GetType().Name} is not a value of column type {type.Name}.", nameof(value));

            return new UntypedConstantTerm(type, value);
        }

    }

}

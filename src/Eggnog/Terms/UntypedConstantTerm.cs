using System;

namespace Eggnog
{

    /// <summary>
    /// An untyped constant of a column type (D13), from <c>Term.Constant</c>.
    /// </summary>
    internal sealed class UntypedConstantTerm : Term
    {

        readonly ColumnType type;
        readonly object value;

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="type">The column type.</param>
        /// <param name="value">The value, of the type's CLR type.</param>
        public UntypedConstantTerm(ColumnType type, object value)
        {
            this.type = type;
            this.value = value;
        }

        /// <inheritdoc />
        public override Type ClrType => type.ClrType;

        /// <inheritdoc />
        internal override TermShape Shape => new(TermShapeKind.Constant, type.ClrType, null, null, type, value, null, null, Array.Empty<Term>());

    }

}

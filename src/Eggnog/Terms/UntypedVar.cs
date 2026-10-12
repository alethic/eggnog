using System;

namespace Eggnog
{

    /// <summary>
    /// An untyped variable of a column type (D13), from the internal <c>RuleBuilder.Var(ColumnType, string)</c>.
    /// </summary>
    internal sealed class UntypedVar : Term
    {

        readonly object owner;
        readonly ColumnType type;

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="owner">The builder that declared the variable.</param>
        /// <param name="type">The variable's column type.</param>
        /// <param name="name">The variable's name.</param>
        public UntypedVar(object owner, ColumnType type, string name)
        {
            this.owner = owner;
            this.type = type;
            Name = name;
        }

        /// <summary>
        /// Gets the variable's name.
        /// </summary>
        public string Name { get; }

        /// <summary>
        /// Gets the variable's column type.
        /// </summary>
        public ColumnType Type => type;

        /// <inheritdoc />
        public override Type ClrType => type.ClrType;

        /// <inheritdoc />
        internal override TermShape Shape => new(TermShapeKind.Variable, type.ClrType, Name, owner, type, null, null, null, Array.Empty<Term>());

    }

}

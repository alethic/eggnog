using System;

namespace Eggnog
{

    /// <summary>
    /// An untyped application of a function or constructor, from <c>FunctionHandle.Apply</c> or
    /// <c>ConstructorHandle.Apply</c>.
    /// </summary>
    internal sealed class UntypedApplicationTerm : Term
    {

        readonly TableHandle table;
        readonly Term[] arguments;

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="table">The function or constructor.</param>
        /// <param name="arguments">One term per key.</param>
        public UntypedApplicationTerm(TableHandle table, Term[] arguments)
        {
            this.table = table;
            this.arguments = arguments;
        }

        /// <inheritdoc />
        public override Type ClrType => table.Output!.ClrType;

        /// <inheritdoc />
        internal override TermShape Shape => new(TermShapeKind.Application, ClrType, null, null, null, null, table, null, arguments);

    }

}

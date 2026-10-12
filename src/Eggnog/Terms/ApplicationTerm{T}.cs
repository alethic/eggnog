namespace Eggnog
{

    /// <summary>
    /// A typed application of a function: its value at the arguments.
    /// </summary>
    /// <typeparam name="T">The CLR type of the function's values.</typeparam>
    internal sealed class ApplicationTerm<T> : Term<T>
    {

        readonly TableHandle table;
        readonly Term[] arguments;

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="table">The function.</param>
        /// <param name="arguments">One term per key.</param>
        public ApplicationTerm(TableHandle table, Term[] arguments)
        {
            this.table = table;
            this.arguments = arguments;
        }

        /// <inheritdoc />
        internal override TermShape Shape => new(TermShapeKind.Application, typeof(T), null, null, null, null, table, null, arguments);

    }

}

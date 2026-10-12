namespace Eggnog
{

    /// <summary>
    /// A typed application of a primitive.
    /// </summary>
    /// <typeparam name="T">The CLR type of the result.</typeparam>
    internal sealed class ComputeTerm<T> : Term<T>
    {

        readonly Primitive primitive;
        readonly Term[] arguments;

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="primitive">The primitive.</param>
        /// <param name="arguments">One term per parameter.</param>
        public ComputeTerm(Primitive primitive, Term[] arguments)
        {
            this.primitive = primitive;
            this.arguments = arguments;
        }

        /// <inheritdoc />
        internal override TermShape Shape => new(TermShapeKind.Computation, typeof(T), null, null, null, null, null, primitive, arguments);

    }

}

using System;

namespace Eggnog
{

    /// <summary>
    /// An untyped application of a primitive (D13), from the internal <c>Compute.Of(Primitive, …)</c>.
    /// </summary>
    internal sealed class UntypedComputeTerm : Term
    {

        readonly Primitive primitive;
        readonly Term[] arguments;

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="primitive">The primitive.</param>
        /// <param name="arguments">One term per parameter.</param>
        public UntypedComputeTerm(Primitive primitive, Term[] arguments)
        {
            this.primitive = primitive;
            this.arguments = arguments;
        }

        /// <inheritdoc />
        public override Type ClrType => primitive.ResultType;

        /// <inheritdoc />
        internal override TermShape Shape => new(TermShapeKind.Computation, ClrType, null, null, null, null, null, primitive, arguments);

    }

}

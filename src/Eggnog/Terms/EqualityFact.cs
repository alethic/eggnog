namespace Eggnog
{

    /// <summary>
    /// The fact that two terms are equal.
    /// </summary>
    internal sealed class EqualityFact : Fact
    {

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="left">One side.</param>
        /// <param name="right">The other side.</param>
        public EqualityFact(Term left, Term right)
        {
            Left = left;
            Right = right;
        }

        /// <summary>
        /// Gets the left side.
        /// </summary>
        public Term Left { get; }

        /// <summary>
        /// Gets the right side.
        /// </summary>
        public Term Right { get; }

        /// <inheritdoc />
        internal override FactKind Kind => FactKind.Equality;

    }

}

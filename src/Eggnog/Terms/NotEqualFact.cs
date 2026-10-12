namespace Eggnog
{

    /// <summary>
    /// The filter that two terms differ: in a rule body only.
    /// </summary>
    internal sealed class NotEqualFact : Fact
    {

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="left">One side.</param>
        /// <param name="right">The other side.</param>
        public NotEqualFact(Term left, Term right)
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
        internal override FactKind Kind => FactKind.NotEqual;

    }

}

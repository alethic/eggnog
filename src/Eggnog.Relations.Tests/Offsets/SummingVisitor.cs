namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// An allocation-free <see cref="IOffsetVisitor"/> that counts and sums the row ids it is given.
    /// </summary>
    struct SummingVisitor : IOffsetVisitor
    {

        /// <summary>
        /// The number of row ids visited.
        /// </summary>
        public int Count;

        /// <summary>
        /// The sum of the row ids visited.
        /// </summary>
        public long Sum;

        /// <inheritdoc />
        public void Visit(int row)
        {
            Count++;
            Sum += row;
        }

    }

}

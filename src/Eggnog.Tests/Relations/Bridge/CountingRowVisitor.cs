namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// A specialized <see cref="IFunctionRowVisitor"/> that counts rows and stops after a limit.
    /// </summary>
    struct CountingRowVisitor : IFunctionRowVisitor
    {

        readonly int limit;

        /// <summary>
        /// Initializes a visitor that stops after <paramref name="limit"/> rows.
        /// </summary>
        public CountingRowVisitor(int limit)
        {
            this.limit = limit;
            Count = 0;
        }

        /// <summary>
        /// Gets the number of rows visited.
        /// </summary>
        public int Count { get; private set; }

        /// <inheritdoc />
        public bool Visit(FunctionRow row)
        {
            Count++;
            return Count < limit;
        }

    }

}

using System;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// An <see cref="ITableOperation"/> that throws <see cref="InvalidOperationException"/> when it runs on
    /// <see cref="Failing"/> and reports every other table changed.
    /// </summary>
    readonly struct ThrowingOperation : ITableOperation
    {

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="failing">The table the operation throws on.</param>
        public ThrowingOperation(TableId failing)
        {
            Failing = failing;
        }

        /// <summary>
        /// Gets the table the operation throws on.
        /// </summary>
        public TableId Failing { get; }

        /// <inheritdoc />
        public bool Run(TableId table, TableInfo info, DatabaseView view)
        {
            if (table == Failing)
                throw new InvalidOperationException($"The operation failed on {table}.");

            return true;
        }

    }

}

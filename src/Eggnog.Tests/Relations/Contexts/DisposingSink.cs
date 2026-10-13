using System;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// An <see cref="IRowSink"/> that tries to dispose a context from inside a scan of it, and records what that threw,
    /// to check that a dispose during a scan of the context is refused.
    /// </summary>
    struct DisposingSink : IRowSink
    {

        readonly Context context;

        /// <summary>
        /// Initializes a sink that disposes <paramref name="context"/> at its first row.
        /// </summary>
        public DisposingSink(Context context)
        {
            this.context = context;
            Error = null;
        }

        /// <summary>
        /// Gets what the dispose threw, or <c>null</c>.
        /// </summary>
        public Exception? Error { get; private set; }

        /// <inheritdoc />
        public void AddRow(int rowId, ReadOnlySpan<uint> row)
        {
            try
            {
                context.Dispose();
            }
            catch (Exception e)
            {
                Error ??= e;
            }
        }

    }

}

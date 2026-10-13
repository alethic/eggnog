using System;
using System.Runtime.CompilerServices;

namespace Eggnog.Relations
{

    /// <summary>
    /// Records whether a scan produced any row: the sink of the executor's existence checks for atoms without
    /// variables.
    /// </summary>
    internal struct ExistenceSink : IRowSink
    {

        bool found;

        /// <summary>
        /// Gets whether a row was seen.
        /// </summary>
        public readonly bool Found => found;

        /// <inheritdoc />
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void AddRow(int rowId, ReadOnlySpan<uint> row)
        {
            found = true;
        }

    }

}

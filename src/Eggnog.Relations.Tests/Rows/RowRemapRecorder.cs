using System;
using System.Collections.Generic;

namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// An <see cref="IRowRemap"/> that records every call, like the closures in egglog's row buffer tests.
    /// </summary>
    readonly struct RowRemapRecorder : IRowRemap
    {

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        public RowRemapRecorder()
        {
            Calls = new List<(uint[] Row, int Old, int New)>();
        }

        /// <summary>
        /// Gets the recorded calls: a copy of the row, its old id and its new id.
        /// </summary>
        public List<(uint[] Row, int Old, int New)> Calls { get; }

        /// <inheritdoc />
        public void Remap(ReadOnlySpan<uint> row, int oldRow, int newRow)
        {
            Calls.Add((row.ToArray(), oldRow, newRow));
        }

    }

}

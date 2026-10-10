using System;

namespace Eggnog.Storage
{

    /// <summary>
    /// Receives one row of a scan: the key columns followed by the output column, if the table has one.
    /// </summary>
    /// <remarks>
    /// The span points into the store's own memory and is valid only for the duration of the call. The store
    /// must not be modified while a scan is in progress.
    /// </remarks>
    /// <param name="row">The row.</param>
    public delegate void RowAction(ReadOnlySpan<ulong> row);

}

using System;

namespace Eggnog.Storage
{

    /// <summary>
    /// Combines two values a function has been given for one key.
    /// </summary>
    /// <remarks>
    /// The result has to be the same whatever order values arrive in and however often each arrives, so the
    /// function must be associative, commutative and idempotent: a lattice join or meet. That is what lets a
    /// context combine its own value with one an ancestor learns later, without replaying anything.
    /// </remarks>
    /// <param name="current">The value already held.</param>
    /// <param name="incoming">The value being added.</param>
    /// <returns>The combined value.</returns>
    public delegate ulong MergeFunction(ulong current, ulong incoming);

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

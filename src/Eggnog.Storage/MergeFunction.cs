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

}

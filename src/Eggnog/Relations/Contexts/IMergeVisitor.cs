namespace Eggnog.Relations
{

    /// <summary>
    /// Receives the merges a child context made over its parent's ids: <see cref="Context.ScanMerges{TVisitor}"/> calls
    /// <see cref="Visit"/> once per (member, canonical) pair, read from the member lists the context's union-find
    /// keeps.
    /// </summary>
    /// <remarks>
    /// Implement it on a struct and pass it by reference, so the scan is specialized for it. The visitor may read any
    /// context (<see cref="Context.Find"/>, say). It runs as a callback of the scanned context's thread domain: writes
    /// to a context of that domain, rebuilds with work to do and structural calls are refused there, and a frozen
    /// context cannot be disposed while it is scanned.
    /// </remarks>
    internal interface IMergeVisitor
    {

        /// <summary>
        /// Visits one member of a class the context merged.
        /// </summary>
        /// <param name="member">An id canonical in the parent and not local to the context; a class's canonical id is
        /// visited paired with itself when it is such an id.</param>
        /// <param name="canonical">The class's canonical id in the context.</param>
        void Visit(uint member, uint canonical);

    }

}

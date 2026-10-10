namespace Eggnog.Relations
{

    /// <summary>
    /// Receives the row ids of a dense range or a sorted run, one at a time and in ascending order. The C# form of
    /// the <c>impl FnMut(RowId)</c> closures egglog passes to <c>Offsets::offsets</c> and
    /// <c>SubsetRef::iter_bounded</c>.
    /// </summary>
    /// <remarks>
    /// Implement it on a struct and pass it by <see langword="ref"/> to the generic methods that take a
    /// <c>TVisitor : struct, IOffsetVisitor</c>: the JIT then specializes the loop per visitor and inlines
    /// <see cref="Visit"/>, the way Rust monomorphizes a closure. Passing by reference lets the visitor accumulate
    /// state across calls.
    /// </remarks>
    internal interface IOffsetVisitor
    {

        /// <summary>
        /// Visits one row id.
        /// </summary>
        /// <param name="row">The row id.</param>
        void Visit(int row);

    }

}

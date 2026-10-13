namespace Eggnog.Relations
{

    /// <summary>
    /// Decides which row ids a <c>Retain</c> keeps. The C# form of the <c>impl FnMut(RowId) -&gt; bool</c> closures
    /// egglog passes to <c>Subset::retain</c> and <c>SortedOffsetVector::retain</c>.
    /// </summary>
    /// <remarks>
    /// Implement it on a struct and pass it by <see langword="ref"/>; the JIT specializes the retain loop per filter
    /// and inlines <see cref="Keep"/>. The filter is called exactly once per row id, in ascending order.
    /// </remarks>
    internal interface IOffsetFilter
    {

        /// <summary>
        /// Gets whether <paramref name="row"/> stays.
        /// </summary>
        /// <param name="row">The row id.</param>
        /// <returns><see langword="true"/> to keep the row.</returns>
        bool Keep(int row);

    }

}

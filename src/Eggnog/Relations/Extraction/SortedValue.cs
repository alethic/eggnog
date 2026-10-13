namespace Eggnog.Relations
{

    /// <summary>
    /// A value together with its sort: an element of a container as <see cref="ContainerSort.AppendInnerValues"/> lists
    /// it, egglog's <c>(ArcSort, Value)</c> pair (src/sort/mod.rs:96-104).
    /// </summary>
    /// <param name="Sort">The element's sort.</param>
    /// <param name="Value">The element.</param>
    internal readonly record struct SortedValue(Sort Sort, Value Value);

}

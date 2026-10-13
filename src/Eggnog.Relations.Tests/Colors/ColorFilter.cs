namespace Eggnog.Relations.Tests
{

    /// <summary>
    /// A filter of a <see cref="ColorRule"/>'s body over two id variables, applied after the join (a primitive, as
    /// egglog's <c>query_prim</c>): the history family's non-monotone conditions (colored-contexts spec §17 M10.3).
    /// </summary>
    /// <param name="Kind">The condition.</param>
    /// <param name="A">The first variable.</param>
    /// <param name="B">The second variable.</param>
    readonly record struct ColorFilter(ColorFilterKind Kind, int A, int B);

}

namespace Eggnog.Tests.Support
{

    /// <summary>
    /// A second e-class sort, of lists of <see cref="Node"/>s, for the tests that need two.
    /// </summary>
    /// <param name="Id">The class.</param>
    public readonly record struct NodeList(ClassId Id) : ISort<NodeList>
    {

        /// <inheritdoc />
        public static NodeList FromId(ClassId id) => new(id);

    }

}

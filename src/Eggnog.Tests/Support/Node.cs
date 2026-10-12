namespace Eggnog.Tests.Support
{

    /// <summary>
    /// The e-class sort of the targeted tests.
    /// </summary>
    /// <param name="Id">The class.</param>
    public readonly record struct Node(ClassId Id) : ISort<Node>
    {

        /// <inheritdoc />
        public static Node FromId(ClassId id) => new(id);

    }

}

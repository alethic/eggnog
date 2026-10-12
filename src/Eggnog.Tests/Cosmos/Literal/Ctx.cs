namespace Eggnog.Tests.Cosmos.Literal
{

    /// <summary>
    /// A context of the literal encoding: <c>(Schema)</c>, <c>(Query)</c> or <c>(Assume c p v)</c>, an e-class sort.
    /// </summary>
    /// <param name="Id">The class.</param>
    public readonly record struct Ctx(ClassId Id) : ISort<Ctx>
    {

        /// <inheritdoc />
        public static Ctx FromId(ClassId id) => new(id);

    }

}

namespace Eggnog.Tests.Cosmos
{

    /// <summary>
    /// A predicate over a document: the e-class sort of the pushdown theory.
    /// </summary>
    /// <param name="Id">The class.</param>
    public readonly record struct Pred(ClassId Id) : ISort<Pred>
    {

        /// <inheritdoc />
        public static Pred FromId(ClassId id) => new(id);

    }

}

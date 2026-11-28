namespace Eggnog.Benchmarks.Library.Predicates
{

    /// <summary>
    /// A predicate over a document, the e-class sort of <see cref="PredicateTheory"/>.
    /// </summary>
    /// <param name="Id">The class.</param>
    public readonly record struct Pred(ClassId Id) : ISort<Pred>
    {

        /// <inheritdoc />
        public static Pred FromId(ClassId id) => new(id);

    }

}

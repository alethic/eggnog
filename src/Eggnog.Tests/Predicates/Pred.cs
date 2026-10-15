namespace Eggnog.Tests.Predicates
{

    /// <summary>
    /// A predicate over a document, the e-class sort of the predicate theories (<see cref="Scoped.PredicateTheory"/>
    /// and <see cref="Literal.ContextArgumentTheory"/>).
    /// </summary>
    /// <param name="Id">The class.</param>
    public readonly record struct Pred(ClassId Id) : ISort<Pred>
    {

        /// <inheritdoc />
        public static Pred FromId(ClassId id) => new(id);

    }

}

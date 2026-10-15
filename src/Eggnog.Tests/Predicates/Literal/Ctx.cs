namespace Eggnog.Tests.Predicates.Literal
{

    /// <summary>
    /// A context of <see cref="ContextArgumentTheory"/>, an e-class sort: <c>(Schema)</c>, <c>(Query)</c> or a case
    /// <c>(Assume c p v)</c>.
    /// </summary>
    /// <param name="Id">The class.</param>
    public readonly record struct Ctx(ClassId Id) : ISort<Ctx>
    {

        /// <inheritdoc />
        public static Ctx FromId(ClassId id) => new(id);

    }

}

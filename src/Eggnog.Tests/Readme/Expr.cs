namespace Eggnog.Tests.Readme
{

    /// <summary>
    /// The e-class sort of the README examples: an arithmetic expression. The declaration is the README's, verbatim.
    /// </summary>
    /// <param name="Id">The class.</param>
    public readonly record struct Expr(ClassId Id) : ISort<Expr>
    {

        /// <summary>
        /// Wraps a class identifier the graph returns.
        /// </summary>
        /// <param name="id">The class.</param>
        /// <returns>The expression.</returns>
        public static Expr FromId(ClassId id) => new(id);

    }

}

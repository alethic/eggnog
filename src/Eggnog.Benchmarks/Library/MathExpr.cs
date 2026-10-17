namespace Eggnog.Benchmarks.Library
{

    /// <summary>
    /// The sort <c>Math</c> of egglog's <c>tests/math-microbenchmark.egg</c>: a class of equivalent math expressions.
    /// </summary>
    /// <param name="Id">The class.</param>
    internal readonly record struct MathExpr(ClassId Id) : ISort<MathExpr>
    {

        /// <summary>
        /// Wraps a class as a <see cref="MathExpr"/>.
        /// </summary>
        /// <param name="id">The class.</param>
        /// <returns>The sort value.</returns>
        public static MathExpr FromId(ClassId id) => new(id);

    }

}
